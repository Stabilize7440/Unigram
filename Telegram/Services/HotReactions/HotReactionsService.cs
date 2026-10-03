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
using Telegram.Common;
using Telegram.Td.Api;

namespace Telegram.Services.HotReactions
{
    public class HotReactionsService : IDisposable
    {
        private static readonly Lazy<HotReactionsService> _current = new(() => new HotReactionsService(), System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);
        public static HotReactionsService Current => _current.Value;

        private readonly HotReactionsDatabase _database;
        private readonly ConcurrentDictionary<long, (CancellationTokenSource Cts, Task WorkerTask)> _crawlers = new();
        private readonly ConcurrentDictionary<long, int> _crawlerEpochs = new();
        private readonly ConcurrentDictionary<long, object> _chatLocks = new();
        private readonly ConcurrentDictionary<long, Action<string, bool>> _progressCallbacks = new();
        private readonly Channel<Action> _dbWriteChannel = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });

        private object GetChatLock(long chatId) => _chatLocks.GetOrAdd(chatId, _ => new object());

        private bool ExecuteIfEpochValid(long chatId, int expectedEpoch, Action action)
        {
            lock (GetChatLock(chatId))
            {
                if (_crawlerEpochs.TryGetValue(chatId, out var ep) && ep == expectedEpoch)
                {
                    action();
                    return true;
                }
                return false;
            }
        }

        public HotReactionsDatabase Database => _database;

        public void SetCrawlerProgressCallback(long chatId, Action<string, bool> callback)
        {
            if (callback != null)
            {
                _progressCallbacks[chatId] = callback;
            }
            else
            {
                _progressCallbacks.TryRemove(chatId, out _);
            }
        }

        private void NotifyCrawlerProgress(long chatId, string status, bool isRunning)
        {
            if (_progressCallbacks.TryGetValue(chatId, out var callback))
            {
                callback?.Invoke(status, isRunning);
            }
        }

        public HotReactionsService()
        {
            _database = new HotReactionsDatabase();
            Task.Run(() => _database.Initialize());
            Task.Run(ProcessDbWritesAsync);
        }

        private async Task ProcessDbWritesAsync()
        {
            var reader = _dbWriteChannel.Reader;
            while (await reader.WaitToReadAsync())
            {
                while (reader.TryRead(out var action))
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        Logger.Exception(ex);
                    }
                }
            }
        }

        public static (int TopCount, string TopEmoji, string ReactionsJson) ExtractReactions(MessageInteractionInfo info)
        {
            if (info?.Reactions?.Reactions == null || info.Reactions.Reactions.Count == 0)
            {
                return (0, null, null);
            }

            var dict = new Dictionary<string, int>();
            int maxCount = 0;
            string topEmoji = "👍";

            foreach (var r in info.Reactions.Reactions)
            {
                if (r == null || r.TotalCount <= 0) continue;

                string emoji = "👍";
                if (r.Type is ReactionTypeEmoji emojiType)
                {
                    emoji = emojiType.Emoji;
                }
                else if (r.Type is ReactionTypeCustomEmoji)
                {
                    emoji = "⭐";
                }

                if (dict.TryGetValue(emoji, out int current))
                {
                    dict[emoji] = current + r.TotalCount;
                }
                else
                {
                    dict[emoji] = r.TotalCount;
                }

                if (dict[emoji] > maxCount)
                {
                    maxCount = dict[emoji];
                    topEmoji = emoji;
                }
            }

            if (maxCount <= 0)
            {
                return (0, null, null);
            }

            string json = System.Text.Json.JsonSerializer.Serialize(dict);
            return (maxCount, topEmoji, json);
        }

        public static (int Count, string Emoji) GetTopReaction(MessageInteractionInfo info)
        {
            var (count, emoji, _) = ExtractReactions(info);
            return (count, emoji);
        }

        public async Task<(long MsgId, long Date)> GetEarliestMessageAsync(IClientService clientService, long chatId)
        {
            try
            {
                var response = await clientService.SendAsync(new GetChatHistory(chatId, 1, -1, 1, false));
                if (response is Messages messages && messages.MessagesValue.Count > 0)
                {
                    var firstMsg = messages.MessagesValue[0];
                    return (firstMsg.Id, firstMsg.Date);
                }
            }
            catch (Exception ex)
            {
                Logger.Exception(ex);
            }
            return (0, 0);
        }

        public async Task<List<HotMessageItem>> SyncHotWindowAsync(IClientService clientService, long chatId, Action<string> progressCallback = null)
        {
            // 为避免同一个 Chat 的 GetChatHistory 请求在 TDLib 内部发生并发冲突或排队挂起，
            // 若后台慢爬正在运行，先暂时停止
            bool crawlerWasRunning = IsCrawlerRunning(chatId);
            if (crawlerWasRunning)
            {
                StopColdCrawler(chatId);
            }

            int currentEpoch = _crawlerEpochs.GetOrAdd(chatId, 1);

            try
            {
                return await Task.Run(async () =>
                {
                    var state = _database.GetSyncState(chatId);
                    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var sevenDaysAgo = now - (7 * 86400);

                    var itemsToSave = new List<HotMessageItem>();
                    long fromMessageId = 0;
                    int totalFetched = 0;
                    bool reachedEnd = false;

                    progressCallback?.Invoke("正在同步近7天消息...");

                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

                    while (totalFetched < 500 && !reachedEnd && !timeoutCts.IsCancellationRequested)
                    {
                        var sendTask = clientService.SendAsync(new GetChatHistory(chatId, fromMessageId, 0, 100, false));
                        var completed = await Task.WhenAny(sendTask, Task.Delay(4000, timeoutCts.Token));

                        if (completed != sendTask)
                        {
                            // 单次请求超时（4秒），防止 TDLib 内部死锁或无响应
                            break;
                        }

                        var response = await sendTask;
                        if (response is not Messages messages || messages.MessagesValue.Count == 0)
                        {
                            reachedEnd = true;
                            break;
                        }

                        long oldestInBatch = fromMessageId;

                        foreach (var msg in messages.MessagesValue)
                        {
                            totalFetched++;
                            oldestInBatch = msg.Id;

                            if (msg.Date < sevenDaysAgo)
                            {
                                reachedEnd = true;
                            }

                            var (count, emoji, reactionsJson) = ExtractReactions(msg.InteractionInfo);
                            if (count > 0 && state.SampleCount < 50)
                            {
                                state.SampleCount++;
                                state.SampleSum += count;
                                if (state.SampleCount == 50)
                                {
                                    double avg = (double)state.SampleSum / state.SampleCount;
                                    state.ComputedThreshold = Math.Max(1, (int)Math.Floor(avg * 0.9));
                                }
                            }

                            if (count > 0)
                            {
                                var (snippet, hasMedia) = ExtractSnippet(msg.Content);
                                itemsToSave.Add(new HotMessageItem
                                {
                                    ChatId = chatId,
                                    MessageId = msg.Id,
                                    Date = msg.Date,
                                    MaxReactionCount = count,
                                    TopEmoji = emoji,
                                    ReactionsJson = reactionsJson,
                                    Snippet = snippet,
                                    SenderName = GetSenderDisplayName(clientService, msg.SenderId),
                                    HasMedia = hasMedia,
                                    IsCold = msg.Date < sevenDaysAgo
                                });
                            }
                        }

                        // 关键防停滞/历史触底检测：
                        // 当频道历史到头时，TDLib 不会返回空列表，而是返回包含起点自身的单条记录
                        if (oldestInBatch == fromMessageId && fromMessageId != 0)
                        {
                            reachedEnd = true;
                            break;
                        }

                        fromMessageId = oldestInBatch;
                    }

                    bool writeOk = ExecuteIfEpochValid(chatId, currentEpoch, () =>
                    {
                        if (itemsToSave.Count > 0)
                        {
                            _database.UpsertMessages(chatId, itemsToSave);
                        }

                        _database.FreezeMessagesOlderThan(chatId, sevenDaysAgo);
                        _database.PruneColdMessages(chatId, state.EffectiveThreshold, sevenDaysAgo);

                        state.LastHotSync = now;
                        if (state.NewestSyncedMsgId == 0 && itemsToSave.Count > 0)
                        {
                            state.NewestSyncedMsgId = itemsToSave[0].MessageId;
                        }
                        if (state.OldestSyncedMsgId == 0 && fromMessageId != 0)
                        {
                            state.OldestSyncedMsgId = fromMessageId;
                        }

                        _database.SaveSyncState(state);
                    });

                    if (!writeOk)
                    {
                        return new List<HotMessageItem>();
                    }

                    return _database.GetTopMessages(chatId, 100, null, state.EffectiveThreshold);
                });
            }
            catch (Exception ex)
            {
                Logger.Exception(ex);
                return await Task.Run(() => _database.GetTopMessages(chatId, 100, null, 1));
            }
        }

        public bool IsCrawlerRunning(long chatId)
        {
            return _crawlers.TryGetValue(chatId, out var pair) && !pair.Cts.IsCancellationRequested;
        }

        public Task StopColdCrawler(long chatId)
        {
            _crawlerEpochs.AddOrUpdate(chatId, 1, (_, v) => v + 1);

            if (_crawlers.TryRemove(chatId, out var pair))
            {
                try
                {
                    pair.Cts.Cancel();
                }
                catch { }
                return pair.WorkerTask;
            }
            return Task.CompletedTask;
        }

        public void StartColdCrawler(IClientService clientService, long chatId, Action<string, bool> onProgress, bool forceResume = false)
        {
            StopColdCrawler(chatId);

            if (onProgress != null)
            {
                SetCrawlerProgressCallback(chatId, onProgress);
            }

            int currentEpoch = _crawlerEpochs.AddOrUpdate(chatId, 1, (_, v) => v + 1);
            var cts = new CancellationTokenSource();

            var workerTask = Task.Run(async () =>
            {
                var token = cts.Token;
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var sevenDaysAgo = now - (7 * 86400);
                int consecutiveErrors = 0;
                int stallCount = 0;

                try
                {
                    if (token.IsCancellationRequested || (_crawlerEpochs.TryGetValue(chatId, out var ep0) && ep0 != currentEpoch))
                    {
                        return;
                    }

                    var state = _database.GetSyncState(chatId);
                    if (forceResume && state.ColdSyncCompleted)
                    {
                        state.ColdSyncCompleted = false;
                        ExecuteIfEpochValid(chatId, currentEpoch, () => _database.SaveSyncState(state));
                    }

                    if (state.ColdSyncCompleted)
                    {
                        NotifyCrawlerProgress(chatId, "历史消息已全部扫描完毕", false);
                        return;
                    }

                    // 探针获取频道的最初消息时间与 ID，辅助裁定慢爬是否真正到达历史原点
                    var initState = _database.GetSyncState(chatId);
                    if (initState.EarliestMsgDate == 0)
                    {
                        var (earliestId, earliestDate) = await GetEarliestMessageAsync(clientService, chatId);
                        if (earliestDate > 0)
                        {
                            if (token.IsCancellationRequested)
                            {
                                return;
                            }
                            initState.EarliestMsgId = earliestId;
                            initState.EarliestMsgDate = earliestDate;
                            if (!ExecuteIfEpochValid(chatId, currentEpoch, () => _database.SaveSyncState(initState)))
                            {
                                return;
                            }
                        }
                    }

                    while (!token.IsCancellationRequested)
                    {
                        var currentState = _database.GetSyncState(chatId);
                        if (currentState.ColdSyncCompleted)
                        {
                            NotifyCrawlerProgress(chatId, "历史消息已全部扫描完毕", false);
                            break;
                        }

                        long fromId = currentState.OldestSyncedMsgId;
                        var response = await clientService.SendAsync(new GetChatHistory(chatId, fromId, 0, 100, false));

                        if (token.IsCancellationRequested)
                        {
                            break;
                        }

                        if (response is Telegram.Td.Api.Error error)
                        {
                            consecutiveErrors++;
                            if (error.Message.StartsWith("FLOOD_WAIT"))
                            {
                                int delay = 5;
                                var parts = error.Message.Split('_');
                                if (parts.Length > 2 && int.TryParse(parts[2], out int waitSec))
                                {
                                    delay = Math.Clamp(waitSec, 2, 60);
                                }
                                NotifyCrawlerProgress(chatId, $"触发限流，等待 {delay} 秒后重试...", true);
                                await Task.Delay(delay * 1000, token);
                                continue;
                            }

                            if (consecutiveErrors >= 3)
                            {
                                NotifyCrawlerProgress(chatId, $"慢爬网络暂停：{error.Message}", false);
                                break;
                            }

                            await Task.Delay(3000, token);
                            continue;
                        }

                        consecutiveErrors = 0;

                        // 检查是否返回空批次
                        if (response is not Messages messages || messages.MessagesValue.Count == 0)
                        {
                            // 如果还未确认到达历史起点，说明 TDLib 正在远端加载数据，进行等待重试
                            if (stallCount < 4)
                            {
                                stallCount++;
                                NotifyCrawlerProgress(chatId, $"正在同步历史深处数据 (重试 {stallCount}/4)...", true);
                                await Task.Delay(2000, token);
                                continue;
                            }

                            // 连续重试 4 次依然为空，若确认已在起点附近，则标记完成；否则暂停待续
                            if (fromId == 0 || (currentState.EarliestMsgId > 0 && fromId <= currentState.EarliestMsgId))
                            {
                                currentState.ColdSyncCompleted = true;
                                bool writeOk = ExecuteIfEpochValid(chatId, currentEpoch, () =>
                                {
                                    if (currentState.SampleCount > 0 && currentState.SampleCount < 50)
                                    {
                                        double avg = (double)currentState.SampleSum / currentState.SampleCount;
                                        currentState.ComputedThreshold = Math.Max(1, (int)Math.Floor(avg * 0.9));
                                        _database.PruneColdMessages(chatId, currentState.EffectiveThreshold, sevenDaysAgo);
                                    }
                                    _database.SaveSyncState(currentState);
                                });
                                if (writeOk)
                                {
                                    NotifyCrawlerProgress(chatId, "历史消息已全部扫描完毕", false);
                                }
                            }
                            else
                            {
                                NotifyCrawlerProgress(chatId, "历史数据同步停滞，可稍后点击继续", false);
                            }
                            break;
                        }

                        var coldItems = new List<HotMessageItem>();
                        long oldestInBatch = fromId;
                        long oldestDateInBatch = 0;

                        foreach (var msg in messages.MessagesValue)
                        {
                            oldestInBatch = msg.Id;
                            oldestDateInBatch = msg.Date;

                            var (count, emoji, reactionsJson) = ExtractReactions(msg.InteractionInfo);
                            if (count > 0 && currentState.SampleCount < 50)
                            {
                                currentState.SampleCount++;
                                currentState.SampleSum += count;
                                if (currentState.SampleCount == 50)
                                {
                                    double avg = (double)currentState.SampleSum / currentState.SampleCount;
                                    currentState.ComputedThreshold = Math.Max(1, (int)Math.Floor(avg * 0.9));
                                    ExecuteIfEpochValid(chatId, currentEpoch, () =>
                                    {
                                        _database.PruneColdMessages(chatId, currentState.EffectiveThreshold, sevenDaysAgo);
                                    });
                                }
                            }

                            if (count >= currentState.EffectiveThreshold)
                            {
                                var (snippet, hasMedia) = ExtractSnippet(msg.Content);
                                coldItems.Add(new HotMessageItem
                                {
                                    ChatId = chatId,
                                    MessageId = msg.Id,
                                    Date = msg.Date,
                                    MaxReactionCount = count,
                                    TopEmoji = emoji,
                                    ReactionsJson = reactionsJson,
                                    Snippet = snippet,
                                    SenderName = GetSenderDisplayName(clientService, msg.SenderId),
                                    HasMedia = hasMedia,
                                    IsCold = true
                                });
                            }
                        }

                        bool reachedStart = (currentState.EarliestMsgId > 0 && oldestInBatch <= currentState.EarliestMsgId) ||
                                            (currentState.EarliestMsgDate > 0 && oldestDateInBatch > 0 && oldestDateInBatch <= currentState.EarliestMsgDate);

                        // 若 ID 未推进
                        if (oldestInBatch == fromId && fromId != 0)
                        {
                            if (reachedStart)
                            {
                                currentState.ColdSyncCompleted = true;
                                if (ExecuteIfEpochValid(chatId, currentEpoch, () => _database.SaveSyncState(currentState)))
                                {
                                    NotifyCrawlerProgress(chatId, "历史消息已全部扫描完毕", false);
                                }
                                break;
                            }

                            // 未到起点却未推进：TDLib 本地未命中，正在向云端拉取，等待重试
                            stallCount++;
                            if (stallCount < 4)
                            {
                                NotifyCrawlerProgress(chatId, $"历史深度数据拉取中 (重试 {stallCount}/4)...", true);
                                await Task.Delay(2000, token);
                                continue;
                            }

                            NotifyCrawlerProgress(chatId, "历史数据拉取暂无进展，可稍后点击继续", false);
                            break;
                        }

                        // 正常推进
                        stallCount = 0;
                        currentState.OldestSyncedMsgId = oldestInBatch;

                        if (reachedStart)
                        {
                            currentState.ColdSyncCompleted = true;
                        }

                        bool writeSuccess = ExecuteIfEpochValid(chatId, currentEpoch, () =>
                        {
                            if (coldItems.Count > 0)
                            {
                                _database.UpsertMessages(chatId, coldItems);
                            }
                            _database.SaveSyncState(currentState);
                        });

                        if (!writeSuccess || token.IsCancellationRequested)
                        {
                            break;
                        }

                        if (reachedStart)
                        {
                            NotifyCrawlerProgress(chatId, "历史消息已全部扫描完毕", false);
                            break;
                        }

                        if (token.IsCancellationRequested)
                        {
                            break;
                        }

                        int totalSaved = _database.GetTotalHotCount(chatId);
                        string dateStr = oldestDateInBatch > 0
                            ? DateTimeOffset.FromUnixTimeSeconds(oldestDateInBatch).ToLocalTime().ToString("yyyy/MM/dd")
                            : string.Empty;

                        if (currentState.EarliestMsgDate > 0 && oldestDateInBatch > 0 && now > currentState.EarliestMsgDate)
                        {
                            long totalSpan = Math.Max(1, now - currentState.EarliestMsgDate);
                            long scannedSpan = Math.Max(0, now - oldestDateInBatch);
                            int pct = (int)Math.Clamp((scannedSpan * 100) / totalSpan, 0, 99);
                            string firstDateStr = DateTimeOffset.FromUnixTimeSeconds(currentState.EarliestMsgDate).ToLocalTime().ToString("yyyy/MM/dd");
                            NotifyCrawlerProgress(chatId, $"慢爬中：已扫描至 {dateStr} (起点: {firstDateStr} · 约 {pct}%) · 已收录 {totalSaved} 条", true);
                        }
                        else
                        {
                            NotifyCrawlerProgress(chatId, $"慢爬中：已扫描至 {dateStr} (已收录 {totalSaved} 条)", true);
                        }

                        await Task.Delay(2500, token);
                    }

                    if (token.IsCancellationRequested)
                    {
                        NotifyCrawlerProgress(chatId, "慢爬已暂停", false);
                    }
                }
                catch (OperationCanceledException)
                {
                    NotifyCrawlerProgress(chatId, "慢爬已暂停", false);
                }
                catch (Exception ex)
                {
                    Logger.Exception(ex);
                    NotifyCrawlerProgress(chatId, "慢爬发生异常", false);
                }
                finally
                {
                    if (_crawlers.TryGetValue(chatId, out var existing) && existing.Cts == cts)
                    {
                        _crawlers.TryRemove(chatId, out _);
                    }
                    try
                    {
                        cts.Dispose();
                    }
                    catch { }
                }
            }, cts.Token);

            _crawlers[chatId] = (cts, workerTask);
        }

        public async Task ResetChannelSyncAsync(long chatId)
        {
            var task = StopColdCrawler(chatId);
            if (task != null && !task.IsCompleted)
            {
                try
                {
                    await Task.WhenAny(task, Task.Delay(1000));
                }
                catch { }
            }

            await Task.Run(() =>
            {
                lock (GetChatLock(chatId))
                {
                    _crawlerEpochs.AddOrUpdate(chatId, 1, (_, v) => v + 1);
                    _database.ResetSyncState(chatId);
                }
            });
        }

        public void ResetChannelSync(long chatId)
        {
            StopColdCrawler(chatId);
            lock (GetChatLock(chatId))
            {
                _crawlerEpochs.AddOrUpdate(chatId, 1, (_, v) => v + 1);
                _database.ResetSyncState(chatId);
            }
        }

        public void SetUserCustomThreshold(long chatId, int? threshold)
        {
            var state = _database.GetSyncState(chatId);
            state.CustomThreshold = threshold;
            _database.SaveSyncState(state);

            var sevenDaysAgo = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (7 * 86400);
            _database.PruneColdMessages(chatId, state.EffectiveThreshold, sevenDaysAgo);
        }

        public void UpdateMessageReaction(long chatId, long messageId, MessageInteractionInfo interactionInfo)
        {
            if (interactionInfo == null) return;
            var (count, emoji, reactionsJson) = ExtractReactions(interactionInfo);
            if (count <= 0) return;

            // 投递至单消费者无界通道，严格保序并绝不阻塞 TDLib 专有的 TdReceive 接收工作线程
            _dbWriteChannel.Writer.TryWrite(() =>
            {
                _database.UpdateMessageReactionIfExists(chatId, messageId, count, emoji, reactionsJson);
            });
        }

        public void MarkMessagesDeleted(long chatId, IEnumerable<long> messageIds)
        {
            if (messageIds == null) return;
            var copy = messageIds.ToArray();
            _dbWriteChannel.Writer.TryWrite(() =>
            {
                foreach (var id in copy)
                {
                    _database.MarkMessageDeleted(chatId, id);
                }
            });
        }

        private static (string Snippet, bool HasMedia) ExtractSnippet(MessageContent content)
        {
            if (content is MessageText text)
            {
                return (Truncate(text.Text?.Text), false);
            }
            else if (content is MessagePhoto photo)
            {
                var caption = !string.IsNullOrEmpty(photo.Caption?.Text) ? photo.Caption.Text : "[图片]";
                return (Truncate(caption), true);
            }
            else if (content is MessageVideo video)
            {
                var caption = !string.IsNullOrEmpty(video.Caption?.Text) ? video.Caption.Text : "[视频]";
                return (Truncate(caption), true);
            }
            else if (content is MessageDocument doc)
            {
                var caption = !string.IsNullOrEmpty(doc.Caption?.Text) ? doc.Caption.Text : (doc.Document?.FileName ?? "[文件]");
                return (Truncate(caption), true);
            }
            else if (content is MessageAnimation anim)
            {
                var caption = !string.IsNullOrEmpty(anim.Caption?.Text) ? anim.Caption.Text : "[GIF动图]";
                return (Truncate(caption), true);
            }
            else if (content is MessagePoll poll)
            {
                return ($"[投票] {poll.Poll?.Question}", false);
            }
            return ("[消息]", false);
        }

        private static string Truncate(string str, int maxLen = 100)
        {
            if (string.IsNullOrEmpty(str)) return string.Empty;
            var clean = str.Replace("\r\n", " ").Replace("\n", " ").Trim();
            return clean.Length <= maxLen ? clean : clean.Substring(0, maxLen) + "...";
        }

        private static string GetSenderDisplayName(IClientService clientService, MessageSender sender)
        {
            if (sender is MessageSenderUser user)
            {
                if (clientService.TryGetUser(user.UserId, out var u))
                {
                    return $"{u.FirstName} {u.LastName}".Trim();
                }
            }
            else if (sender is MessageSenderChat chatSender)
            {
                if (clientService.TryGetChat(chatSender.ChatId, out var c))
                {
                    return c.Title ?? string.Empty;
                }
            }
            return string.Empty;
        }

        public void Dispose()
        {
            foreach (var kv in _crawlers)
            {
                try { kv.Value.Cts.Cancel(); kv.Value.Cts.Dispose(); } catch { }
            }
            _crawlers.Clear();
            _database?.Dispose();
        }
    }
}
