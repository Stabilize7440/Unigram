//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Telegram.Td.Api;

namespace Telegram.Services.HotReactions
{
    public class HotReactionsService : IDisposable
    {
        private sealed class Crawler
        {
            public CancellationTokenSource Cancellation;
            public Task Task;
            public Action<string, bool> Progress;
        }

        private sealed class PendingUpdate
        {
            public bool Deleted;
            public MessageInteractionInfo Reactions;
        }

        private readonly IClientService _clientService;
        private readonly HotReactionsDatabase _database;
        private readonly CancellationTokenSource _lifetime = new();
        private readonly SemaphoreSlim _requestGate = new(1, 1);
        private readonly TimeSpan _requestInterval;
        private DateTimeOffset _nextRequest;
        private readonly ConcurrentDictionary<long, Crawler> _crawlers = new();
        private readonly ConcurrentDictionary<long, bool> _paused = new();
        private readonly ConcurrentDictionary<long, int> _epochs = new();
        private readonly ConcurrentDictionary<long, object> _chatLocks = new();
        private readonly ConcurrentDictionary<long, SemaphoreSlim> _syncGates = new();
        private readonly ConcurrentDictionary<(long ChatId, long MessageId), PendingUpdate> _pendingUpdates = new();
        private readonly Channel<bool> _updateSignal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite
        });
        private readonly Task _initialization;
        private readonly Task _updateWorker;
        private readonly object _lifecycleLock = new();
        private readonly HashSet<Task> _workers = new();
        private Task _shutdown = Task.CompletedTask;
        private volatile bool _disposed;

        public HotReactionsDatabase Database => _database;
        public long UserId { get; }
        private bool IsCurrentAccount => UserId == _clientService.Options.MyId;
        public event Action<long> MessagesChanged;
        public event Action<long, string, bool> CrawlerProgress;

        public HotReactionsService(IClientService clientService, Task previousShutdown)
            : this(clientService, new HotReactionsDatabase(clientService.SessionId, clientService.Options.MyId), TimeSpan.FromSeconds(2.5), previousShutdown)
        {
        }

        internal HotReactionsService(IClientService clientService, HotReactionsDatabase database, TimeSpan requestInterval, Task previousShutdown = null)
        {
            _clientService = clientService;
            UserId = clientService.Options.MyId;
            if (UserId <= 0) throw new InvalidOperationException("排行榜需要已登录的账号");
            _database = database;
            _requestInterval = requestInterval;
            _initialization = Task.Run(async () =>
            {
                if (previousShutdown != null) await previousShutdown;
                _database.Initialize();
            });
            _updateWorker = Task.Run(ProcessUpdatesAsync);
        }

        public Task InitializeAsync() => _initialization;
        private object ChatLock(long chatId) => _chatLocks.GetOrAdd(chatId, _ => new object());
        private SemaphoreSlim SyncGate(long chatId) => _syncGates.GetOrAdd(chatId, _ => new SemaphoreSlim(1, 1));
        private int Epoch(long chatId) => _epochs.GetOrAdd(chatId, 0);
        public bool IsCrawlerPaused(long chatId) => _paused.TryGetValue(chatId, out var paused) && paused;
        private bool IsPaused(long chatId) => IsCrawlerPaused(chatId);

        private bool Commit(long chatId, int epoch, CancellationToken token, IReadOnlyList<HotMessageItem> messages,
            Action<ChannelSyncState> advance, long cutoff)
        {
            lock (ChatLock(chatId))
            {
                if (_disposed || !IsCurrentAccount || token.IsCancellationRequested || Epoch(chatId) != epoch) return false;
                var revision = _database.GetRevision(chatId);
                _database.CommitBatch(chatId, messages, advance, cutoff);
                if (_database.GetRevision(chatId) != revision) RaiseMessagesChanged(chatId);
                return true;
            }
        }

        public static (int TopCount, string TopEmoji, string ReactionsJson) ExtractReactions(MessageInteractionInfo info)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            if (info?.Reactions?.Reactions != null)
            {
                foreach (var reaction in info.Reactions.Reactions)
                {
                    if (reaction == null || reaction.TotalCount <= 0) continue;
                    string key;
                    if (reaction.Type is ReactionTypeEmoji emoji) key = emoji.Emoji;
                    else if (reaction.Type is ReactionTypeCustomEmoji custom) key = ReactionSentimentService.CustomReactionPrefix + custom.CustomEmojiId;
                    else if (reaction.Type is ReactionTypePaid) key = ReactionSentimentService.PaidReactionKey;
                    else continue;
                    if (string.IsNullOrEmpty(key)) continue;
                    // A duplicate identity is still one reaction, never an additional sentiment bucket.
                    counts[key] = Math.Max(counts.TryGetValue(key, out var count) ? count : 0, reaction.TotalCount);
                }
            }
            if (counts.Count == 0) return (0, null, null);
            var top = counts.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).First();
            return (top.Value, top.Key, System.Text.Json.JsonSerializer.Serialize(counts));
        }

        public static (int Count, string Emoji) GetTopReaction(MessageInteractionInfo info)
        {
            var (count, emoji, _) = ExtractReactions(info);
            return (count, emoji);
        }

        internal static int FloodWaitSeconds(Error error)
        {
            var message = error.Message ?? string.Empty;
            if (message.StartsWith("FLOOD_WAIT_", StringComparison.Ordinal)
                || message.StartsWith("FLOOD_PREMIUM_WAIT_", StringComparison.Ordinal))
            {
                var prefixLength = message.StartsWith("FLOOD_WAIT_", StringComparison.Ordinal) ? "FLOOD_WAIT_".Length : "FLOOD_PREMIUM_WAIT_".Length;
                var value = message.Substring(prefixLength).Split('_')[0];
                return int.TryParse(value, out var seconds) ? Math.Max(1, seconds) : 5;
            }
            const string retry = "retry after ";
            var offset = message.IndexOf(retry, StringComparison.OrdinalIgnoreCase);
            if (error.Code == 429 && offset >= 0 && int.TryParse(message.Substring(offset + retry.Length).Trim(), out var wait))
            {
                return Math.Max(1, wait);
            }
            return error.Code == 429 ? 5 : 0;
        }

        // The request gate stays owned by a late-reply observer until the native request completes.
        private void DelayRequests(TimeSpan duration)
        {
            var until = DateTimeOffset.UtcNow + duration;
            if (until > _nextRequest) _nextRequest = until;
        }

        private void RespectServerWait(Object response)
        {
            if (_disposed || response is not Error error) return;
            var seconds = FloodWaitSeconds(error);
            if (seconds > 0) DelayRequests(TimeSpan.FromSeconds(seconds));
        }

        private async Task<Object> GetHistoryAsync(long chatId, long fromId, int offset, int limit, CancellationToken token)
        {
            await _requestGate.WaitAsync(token);
            bool releaseGate = true;
            try
            {
                while (true)
                {
                    var remaining = _nextRequest - DateTimeOffset.UtcNow;
                    if (remaining <= TimeSpan.Zero) break;
                    await Task.Delay(remaining > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : remaining, token);
                }
                token.ThrowIfCancellationRequested();
                if (!IsCurrentAccount) throw new InvalidOperationException("排行榜账号已切换");
                DelayRequests(_requestInterval);
                var send = _clientService.SendAsync(new GetChatHistory(chatId, fromId, offset, limit, false));
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                var delay = Task.Delay(TimeSpan.FromSeconds(30), timeout.Token);
                if (await Task.WhenAny(send, delay) != send)
                {
                    // TDLib cannot cancel the native request. Keep its account slot until the reply arrives,
                    // honor late rate limits, and never commit that reply into a canceled scan.
                    releaseGate = false;
                    _ = send.ContinueWith(task =>
                    {
                        try
                        {
                            if (task.IsFaulted) Logger.Exception(task.Exception);
                            else if (task.Status == TaskStatus.RanToCompletion) RespectServerWait(task.Result);
                        }
                        finally { _requestGate.Release(); }
                    }, TaskContinuationOptions.ExecuteSynchronously);
                    token.ThrowIfCancellationRequested();
                    throw new TimeoutException("历史请求超时，请稍后继续同步");
                }
                timeout.Cancel();
                var response = await send;
                RespectServerWait(response);
                return response;
            }
            finally
            {
                if (releaseGate) _requestGate.Release();
            }
        }

        private async Task<(long MsgId, long Date)> EarliestAsync(long chatId, CancellationToken token)
        {
            var response = await GetHistoryAsync(chatId, 1, -1, 1, token);
            if (response is Messages messages && messages.MessagesValue.Count > 0)
            {
                var message = messages.MessagesValue[messages.MessagesValue.Count - 1];
                return (message.Id, message.Date);
            }
            return (0, 0);
        }

        public Task<List<HotMessageItem>> SyncHotWindowAsync(IClientService clientService, long chatId,
            Action<string> progressCallback = null, CancellationToken token = default)
        {
            lock (_lifecycleLock)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(HotReactionsService));
                var task = Task.Run(() => SyncHotWindowCoreAsync(clientService, chatId, progressCallback, token));
                TrackWorker(task);
                return task;
            }
        }

        private void TrackWorker(Task task)
        {
            _workers.Add(task);
            _ = task.ContinueWith(completed =>
            {
                lock (_lifecycleLock) _workers.Remove(completed);
            }, TaskContinuationOptions.ExecuteSynchronously);
        }

        private async Task<List<HotMessageItem>> SyncHotWindowCoreAsync(IClientService clientService, long chatId,
            Action<string> progressCallback, CancellationToken token)
        {
            if (clientService != _clientService) throw new ArgumentException("Wrong account", nameof(clientService));
            await _initialization;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var cancellation = deadline.Token;
            var gate = SyncGate(chatId);
            var acquired = false;
            var epoch = Epoch(chatId);
            try
            {
                await StopCrawler(chatId, false);
                await gate.WaitAsync(cancellation);
                acquired = true;
                if (!Commit(chatId, epoch, cancellation, Array.Empty<HotMessageItem>(), null,
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 7 * 86400)) return new List<HotMessageItem>();
                var state = _database.GetSyncState(chatId);
                long fromId = 0;
                long newestId = 0;
                long stopId = state.NewestSyncedMsgId;
                var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 7 * 86400;
                int fetched = 0;
                progressCallback?.Invoke("正在同步近7天消息...");
                while (fetched < 500)
                {
                    var response = await GetHistoryAsync(chatId, fromId, 0, 100, cancellation);
                    if (response is not Messages messages || messages.MessagesValue.Count == 0) break;
                    var oldest = messages.MessagesValue.Min(x => x.Id);
                    if (oldest <= 0 || (fromId != 0 && oldest >= fromId)) break;
                    if (newestId == 0) newestId = messages.MessagesValue.Max(x => x.Id);
                    var items = CreateItems(chatId, messages, cutoff);
                    bool finished = messages.MessagesValue.Any(x => x.Date < cutoff && (stopId == 0 || x.Id <= stopId));
                    if (!Commit(chatId, epoch, cancellation, items, current =>
                    {
                        AdvanceOldest(current, oldest);
                        current.PendingSyncFromId = finished ? 0 : oldest;
                        current.PendingSyncNewestId = finished ? 0 : newestId;
                        current.PendingSyncStopId = finished ? 0 : stopId;
                        if (finished)
                        {
                            current.NewestSyncedMsgId = Math.Max(current.NewestSyncedMsgId, newestId);
                            current.LastHotSync = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        }
                    }, cutoff)) break;
                    fetched += messages.MessagesValue.Count;
                    fromId = oldest;
                    if (finished) break;
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested && !_lifetime.IsCancellationRequested)
            {
                progressCallback?.Invoke("快速同步预算已用完，剩余消息将在后台补全");
            }
            catch (TimeoutException ex)
            {
                progressCallback?.Invoke(ex.Message);
            }
            finally
            {
                if (acquired) gate.Release();
                if (!_disposed && IsCurrentAccount && !IsPaused(chatId) && Epoch(chatId) == epoch)
                {
                    StartColdCrawler(_clientService, chatId);
                }
            }
            token.ThrowIfCancellationRequested();
            return await Task.Run(() => _database.GetTopMessages(chatId, 100, null, _database.GetSyncState(chatId).EffectiveThreshold, token: token), token);
        }

        private static void AdvanceOldest(ChannelSyncState state, long oldest)
        {
            if (state.OldestSyncedMsgId == 0 || oldest < state.OldestSyncedMsgId) state.OldestSyncedMsgId = oldest;
        }

        private static void FinishPending(ChannelSyncState state)
        {
            state.NewestSyncedMsgId = Math.Max(state.NewestSyncedMsgId, state.PendingSyncNewestId);
            state.PendingSyncFromId = 0;
            state.PendingSyncNewestId = 0;
            state.PendingSyncStopId = 0;
            state.LastHotSync = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        public bool IsCrawlerRunning(long chatId)
        {
            return _crawlers.TryGetValue(chatId, out var crawler) && !crawler.Cancellation.IsCancellationRequested;
        }

        public Task StopColdCrawler(long chatId) => StopCrawler(chatId, true);

        private Task StopCrawler(long chatId, bool pause)
        {
            lock (ChatLock(chatId))
            {
                if (pause) _paused[chatId] = true;
                if (!_crawlers.TryRemove(chatId, out var crawler)) return Task.CompletedTask;
                crawler.Cancellation.Cancel();
                return crawler.Task ?? Task.CompletedTask;
            }
        }

        public void StartColdCrawler(IClientService clientService, long chatId, Action<string, bool> onProgress = null)
        {
            if (clientService != _clientService) throw new ArgumentException("Wrong account", nameof(clientService));
            lock (_lifecycleLock)
            lock (ChatLock(chatId))
            {
                if (_disposed) return;
                _paused[chatId] = false;
                if (_crawlers.TryGetValue(chatId, out var running))
                {
                    if (onProgress != null) running.Progress = onProgress;
                    return;
                }
                var crawler = new Crawler
                {
                    Cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token),
                    Progress = onProgress
                };
                var epoch = Epoch(chatId);
                _crawlers[chatId] = crawler;
                crawler.Task = Task.Run(() => RunCrawlerAsync(chatId, epoch, crawler));
                TrackWorker(crawler.Task);
            }
        }

        private void Progress(long chatId, Crawler crawler, string status, bool running)
        {
            if (!_crawlers.TryGetValue(chatId, out var current) || current != crawler) return;
            try
            {
                crawler.Progress?.Invoke(status, running);
                CrawlerProgress?.Invoke(chatId, status, running);
            }
            catch (Exception ex) { Logger.Exception(ex); }
        }

        private async Task RunCrawlerAsync(long chatId, int epoch, Crawler crawler)
        {
            var token = crawler.Cancellation.Token;
            var gate = SyncGate(chatId);
            int stalls = 0;
            int errors = 0;
            try
            {
                await _initialization;
                while (!token.IsCancellationRequested)
                {
                    await gate.WaitAsync(token);
                    try
                    {
                        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 7 * 86400;
                        if (!_database.IsIndexed(chatId) && !Commit(chatId, epoch, token, Array.Empty<HotMessageItem>(), null, cutoff)) return;
                        var state = _database.GetSyncState(chatId);
                        if (state.EarliestMsgId == 0 || stalls >= 3)
                        {
                            var (earliestId, date) = await EarliestAsync(chatId, token);
                            if (earliestId > 0)
                            {
                                if (!Commit(chatId, epoch, token, Array.Empty<HotMessageItem>(), current =>
                                {
                                    current.EarliestMsgId = earliestId;
                                    current.EarliestMsgDate = date;
                                }, cutoff)) return;
                                state = _database.GetSyncState(chatId);
                            }
                        }
                        bool pending = state.PendingSyncFromId > 0;
                        long fromId = pending ? state.PendingSyncFromId : state.OldestSyncedMsgId;
                        if (state.EarliestMsgId > 0 && fromId > 0 && fromId <= state.EarliestMsgId)
                        {
                            if (!Commit(chatId, epoch, token, Array.Empty<HotMessageItem>(), current =>
                            {
                                if (pending) FinishPending(current);
                                current.ColdSyncCompleted = true;
                            }, cutoff)) return;
                            Progress(chatId, crawler, "历史消息已全部扫描完毕", false);
                            return;
                        }
                        if (!pending && state.ColdSyncCompleted)
                        {
                            Progress(chatId, crawler, "历史消息已全部扫描完毕", false);
                            return;
                        }
                        var response = await GetHistoryAsync(chatId, fromId, 0, 100, token);
                        if (response is Error error)
                        {
                            var wait = FloodWaitSeconds(error);
                            if (wait > 0)
                            {
                                Progress(chatId, crawler, $"触发限流，账号暂停请求 {wait} 秒...", true);
                                continue;
                            }
                            if (++errors >= 3)
                            {
                                Progress(chatId, crawler, $"同步已暂停：{error.Message}", false);
                                return;
                            }
                            await Task.Delay(3000, token);
                            continue;
                        }
                        errors = 0;
                        var batch = response as Messages;
                        var oldest = batch?.MessagesValue.Count > 0 ? batch.MessagesValue.Min(x => x.Id) : 0;
                        if (oldest <= 0 || (fromId != 0 && oldest >= fromId))
                        {
                            if (++stalls > 3)
                            {
                                Progress(chatId, crawler, "历史同步暂无进展，请稍后继续；未标记完成", false);
                                return;
                            }
                            Progress(chatId, crawler, $"正在确认历史边界 ({stalls}/3)...", true);
                            await Task.Delay(2000, token);
                            continue;
                        }
                        stalls = 0;
                        var items = CreateItems(chatId, batch, cutoff);
                        bool pendingFinished = pending && batch.MessagesValue.Any(x => x.Date < cutoff && (state.PendingSyncStopId == 0 || x.Id <= state.PendingSyncStopId));
                        bool reachedStart = state.EarliestMsgId > 0 && oldest <= state.EarliestMsgId;
                        var newest = batch.MessagesValue.Max(x => x.Id);
                        if (!Commit(chatId, epoch, token, items, current =>
                        {
                            AdvanceOldest(current, oldest);
                            if (!pending && fromId == 0 && current.NewestSyncedMsgId == 0) current.NewestSyncedMsgId = newest;
                            if (pending)
                            {
                                if (pendingFinished || reachedStart) FinishPending(current);
                                else current.PendingSyncFromId = oldest;
                            }
                            if (reachedStart) current.ColdSyncCompleted = true;
                        }, cutoff)) return;
                        if (reachedStart)
                        {
                            Progress(chatId, crawler, "历史消息已全部扫描完毕", false);
                            return;
                        }
                        var scannedDate = batch.MessagesValue.Last().Date;
                        var dateText = DateTimeOffset.FromUnixTimeSeconds(scannedDate).ToLocalTime().ToString("yyyy/MM/dd");
                        var origin = string.Empty;
                        if (state.EarliestMsgDate > 0)
                        {
                            var span = Math.Max(1, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - state.EarliestMsgDate);
                            var percent = Math.Max(0, Math.Min(99, 100.0 * (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - scannedDate) / span));
                            var start = DateTimeOffset.FromUnixTimeSeconds(state.EarliestMsgDate).ToLocalTime().ToString("yyyy/MM/dd");
                            origin = $" (起点: {start} · 约 {percent:F0}%)";
                        }
                        var saved = _database.GetTotalHotCount(chatId);
                        Progress(chatId, crawler, $"{(pending ? "补全近期消息" : "慢爬中")}：已扫描至 {dateText}{origin} · 已收录 {saved} 条", true);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.Exception(ex);
                Progress(chatId, crawler, "同步异常，游标未跳过失败批次，可稍后继续", false);
            }
            finally
            {
                lock (ChatLock(chatId))
                {
                    if (_crawlers.TryGetValue(chatId, out var current) && current == crawler) _crawlers.TryRemove(chatId, out _);
                }
                crawler.Cancellation.Dispose();
            }
        }

        public async Task ResetChannelSyncAsync(long chatId)
        {
            var stopped = StopCrawler(chatId, true);
            lock (ChatLock(chatId))
            {
                _epochs.AddOrUpdate(chatId, 1, (_, epoch) => epoch + 1);
            }
            await stopped;
            await _initialization;
            await Task.Run(() =>
            {
                lock (ChatLock(chatId))
                {
                    _database.ResetSyncState(chatId);
                    RaiseMessagesChanged(chatId);
                }
            });
        }

        public bool SetUserCustomThreshold(long chatId, int? threshold, bool rescanConfirmed = false)
        {
            lock (ChatLock(chatId))
            {
                if (!_database.SetCustomThreshold(chatId, threshold, rescanConfirmed)) return false;
                RaiseMessagesChanged(chatId);
                return true;
            }
        }

        public void UpdateMessageReaction(long chatId, long messageId, MessageInteractionInfo interactionInfo)
        {
            if (_disposed || interactionInfo == null) return;
            QueueUpdate(chatId, messageId, new PendingUpdate { Reactions = interactionInfo });
        }

        public void MarkMessagesDeleted(long chatId, IEnumerable<long> messageIds)
        {
            if (_disposed || messageIds == null) return;
            foreach (var messageId in messageIds) QueueUpdate(chatId, messageId, new PendingUpdate { Deleted = true });
        }

        private void QueueUpdate(long chatId, long messageId, PendingUpdate update)
        {
            if (!IsCurrentAccount) return;
            _pendingUpdates.AddOrUpdate((chatId, messageId), update, (_, previous) => previous.Deleted ? previous : update);
            _updateSignal.Writer.TryWrite(true);
        }

        private async Task ProcessUpdatesAsync()
        {
            try
            {
                await _initialization;
                while (await _updateSignal.Reader.WaitToReadAsync(_lifetime.Token))
                {
                    while (_updateSignal.Reader.TryRead(out _)) { }
                    foreach (var group in _pendingUpdates.ToArray().GroupBy(x => x.Key.ChatId))
                    {
                        var pending = new List<KeyValuePair<(long ChatId, long MessageId), PendingUpdate>>();
                        foreach (var pair in group)
                        {
                            if (((ICollection<KeyValuePair<(long ChatId, long MessageId), PendingUpdate>>)_pendingUpdates).Remove(pair)) pending.Add(pair);
                        }
                        if (!IsCurrentAccount || !_database.IsIndexed(group.Key)) continue;
                        try
                        {
                            var deleted = new List<long>();
                            var reactions = new List<(long, int, string, string)>();
                            foreach (var pair in pending)
                            {
                                if (pair.Value.Deleted) deleted.Add(pair.Key.MessageId);
                                else
                                {
                                    var (count, emoji, json) = ExtractReactions(pair.Value.Reactions);
                                    reactions.Add((pair.Key.MessageId, count, emoji, json));
                                }
                            }
                            lock (ChatLock(group.Key))
                            {
                                if (_disposed) return;
                                var revision = _database.GetRevision(group.Key);
                                _database.ApplyUpdates(group.Key, deleted, reactions);
                                if (_database.GetRevision(group.Key) != revision) RaiseMessagesChanged(group.Key);
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Exception(ex);
                            foreach (var pair in pending)
                            {
                                if (pair.Value.Deleted) QueueUpdate(pair.Key.ChatId, pair.Key.MessageId, pair.Value);
                                else _pendingUpdates.TryAdd(pair.Key, pair.Value);
                            }
                            _updateSignal.Writer.TryWrite(true);
                            await Task.Delay(3000, _lifetime.Token);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Logger.Exception(ex); }
        }

        private void RaiseMessagesChanged(long chatId)
        {
            try { MessagesChanged?.Invoke(chatId); }
            catch (Exception ex) { Logger.Exception(ex); }
        }

        private List<HotMessageItem> CreateItems(long chatId, Messages messages, long cutoff)
        {
            var items = new List<HotMessageItem>(messages.MessagesValue.Count);
            foreach (var message in messages.MessagesValue)
            {
                var (count, emoji, json) = ExtractReactions(message.InteractionInfo);
                var (snippet, media) = ExtractSnippet(message.Content);
                items.Add(new HotMessageItem
                {
                    ChatId = chatId, MessageId = message.Id, Date = message.Date,
                    MaxReactionCount = count, TopEmoji = emoji, ReactionsJson = json,
                    Snippet = snippet, HasMedia = media, IsCold = message.Date < cutoff,
                    SenderName = GetSenderDisplayName(_clientService, message.SenderId)
                });
            }
            return items;
        }

        private static (string Snippet, bool HasMedia) ExtractSnippet(MessageContent content)
        {
            if (content is MessageText text) return (Truncate(text.Text?.Text), false);
            if (content is MessagePhoto photo) return (Truncate(string.IsNullOrEmpty(photo.Caption?.Text) ? "[图片]" : photo.Caption.Text), true);
            if (content is MessageVideo video) return (Truncate(string.IsNullOrEmpty(video.Caption?.Text) ? "[视频]" : video.Caption.Text), true);
            if (content is MessageDocument document) return (Truncate(string.IsNullOrEmpty(document.Caption?.Text) ? document.Document?.FileName ?? "[文件]" : document.Caption.Text), true);
            if (content is MessageAnimation animation) return (Truncate(string.IsNullOrEmpty(animation.Caption?.Text) ? "[GIF动图]" : animation.Caption.Text), true);
            if (content is MessagePoll poll) return (Truncate($"[投票] {poll.Poll?.Question}"), false);
            return ("[消息]", false);
        }

        private static string Truncate(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var clean = text.Replace("\r\n", " ").Replace("\n", " ").Trim();
            return clean.Length <= 100 ? clean : clean.Substring(0, 100) + "...";
        }

        private static string GetSenderDisplayName(IClientService clientService, MessageSender sender)
        {
            if (sender is MessageSenderUser user && clientService.TryGetUser(user.UserId, out var value)) return $"{value.FirstName} {value.LastName}".Trim();
            if (sender is MessageSenderChat chat && clientService.TryGetChat(chat.ChatId, out var channel)) return channel.Title ?? string.Empty;
            return string.Empty;
        }

        public Task ShutdownAsync()
        {
            Dispose();
            return _shutdown;
        }

        public void Dispose()
        {
            lock (_lifecycleLock)
            {
                if (_disposed) return;
                _disposed = true;
                _lifetime.Cancel();
                _updateSignal.Writer.TryComplete();
                _pendingUpdates.Clear();
                CrawlerProgress = null;
                MessagesChanged = null;
                var workers = _workers.Concat(new[] { _initialization, _updateWorker }).ToArray();
                _shutdown = Task.WhenAll(workers).ContinueWith(task =>
                {
                    if (task.IsFaulted) Logger.Exception(task.Exception);
                    _pendingUpdates.Clear();
                    _database.Dispose();
                    _lifetime.Dispose();
                });
            }
        }
    }
}
