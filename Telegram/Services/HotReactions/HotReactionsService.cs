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
using System.Threading.Tasks;
using Telegram.Common;
using Telegram.Td.Api;

namespace Telegram.Services.HotReactions
{
    public class HotReactionsService : IDisposable
    {
        private static HotReactionsService _current;
        public static HotReactionsService Current => _current ??= new HotReactionsService();

        private readonly HotReactionsDatabase _database;
        private readonly ConcurrentDictionary<long, CancellationTokenSource> _crawlers = new();

        public HotReactionsDatabase Database => _database;

        public HotReactionsService()
        {
            _database = new HotReactionsDatabase();
            _database.Initialize();
        }

        public static (int Count, string Emoji) GetTopReaction(MessageInteractionInfo info)
        {
            if (info?.Reactions?.Reactions == null || info.Reactions.Reactions.Count == 0)
            {
                return (0, null);
            }

            var top = info.Reactions.Reactions
                .OrderByDescending(x => x.TotalCount)
                .FirstOrDefault();

            if (top == null || top.TotalCount <= 0)
            {
                return (0, null);
            }

            string emoji = "👍";
            if (top.Type is ReactionTypeEmoji emojiType)
            {
                emoji = emojiType.Emoji;
            }
            else if (top.Type is ReactionTypeCustomEmoji)
            {
                emoji = "⭐";
            }

            return (top.TotalCount, emoji);
        }

        public async Task<List<HotMessageItem>> SyncHotWindowAsync(IClientService clientService, long chatId, Action<string> progressCallback = null)
        {
            var state = _database.GetSyncState(chatId);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var sevenDaysAgo = now - (7 * 86400);

            var itemsToSave = new List<HotMessageItem>();
            long fromMessageId = 0;
            int totalFetched = 0;
            bool reachedEnd = false;

            progressCallback?.Invoke("正在同步近7天消息...");

            while (totalFetched < 500 && !reachedEnd)
            {
                var response = await clientService.SendAsync(new GetChatHistory(chatId, fromMessageId, 0, 100, false));
                if (response is not Messages messages || messages.MessagesValue.Count == 0)
                {
                    reachedEnd = true;
                    break;
                }

                foreach (var msg in messages.MessagesValue)
                {
                    totalFetched++;

                    if (msg.Date < sevenDaysAgo)
                    {
                        reachedEnd = true;
                    }

                    var (count, emoji) = GetTopReaction(msg.InteractionInfo);
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
                            Snippet = snippet,
                            SenderName = GetSenderDisplayName(clientService, msg.SenderId),
                            HasMedia = hasMedia,
                            IsCold = msg.Date < sevenDaysAgo
                        });
                    }

                    fromMessageId = msg.Id;
                }
            }

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

            return _database.GetTopMessages(chatId, 100, null, state.EffectiveThreshold);
        }

        public bool IsCrawlerRunning(long chatId)
        {
            return _crawlers.TryGetValue(chatId, out var cts) && !cts.IsCancellationRequested;
        }

        public void StopColdCrawler(long chatId)
        {
            if (_crawlers.TryRemove(chatId, out var cts))
            {
                try
                {
                    cts.Cancel();
                    cts.Dispose();
                }
                catch { }
            }
        }

        public void StartColdCrawler(IClientService clientService, long chatId, Action<string, bool> onProgress, bool forceResume = false)
        {
            StopColdCrawler(chatId);

            var state = _database.GetSyncState(chatId);
            if (forceResume && state.ColdSyncCompleted)
            {
                state.ColdSyncCompleted = false;
                _database.SaveSyncState(state);
            }

            if (state.ColdSyncCompleted)
            {
                onProgress?.Invoke("历史消息已全部扫描完毕", false);
                return;
            }

            var cts = new CancellationTokenSource();
            _crawlers[chatId] = cts;

            Task.Run(async () =>
            {
                var token = cts.Token;
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var sevenDaysAgo = now - (7 * 86400);
                int consecutiveErrors = 0;
                int emptyBatchCount = 0;

                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        var currentState = _database.GetSyncState(chatId);
                        if (currentState.ColdSyncCompleted)
                        {
                            onProgress?.Invoke("历史消息已全部扫描完毕", false);
                            break;
                        }

                        long fromId = currentState.OldestSyncedMsgId;
                        var response = await clientService.SendAsync(new GetChatHistory(chatId, fromId, 0, 100, false));

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
                                onProgress?.Invoke($"触发限流，等待 {delay} 秒后重试...", true);
                                await Task.Delay(delay * 1000, token);
                                continue;
                            }

                            if (consecutiveErrors >= 3)
                            {
                                onProgress?.Invoke($"慢爬网络暂停：{error.Message}", false);
                                break;
                            }

                            await Task.Delay(3000, token);
                            continue;
                        }

                        consecutiveErrors = 0;

                        if (response is not Messages messages || messages.MessagesValue.Count == 0)
                        {
                            emptyBatchCount++;
                            // 防抖：连续两次返回 0 条且非起点，确认真正到达历史开端
                            if (emptyBatchCount < 2 && fromId != 0)
                            {
                                await Task.Delay(1500, token);
                                continue;
                            }

                            currentState.ColdSyncCompleted = true;

                            if (currentState.SampleCount > 0 && currentState.SampleCount < 50)
                            {
                                double avg = (double)currentState.SampleSum / currentState.SampleCount;
                                currentState.ComputedThreshold = Math.Max(1, (int)Math.Floor(avg * 0.9));
                                _database.PruneColdMessages(chatId, currentState.EffectiveThreshold, sevenDaysAgo);
                            }

                            _database.SaveSyncState(currentState);
                            onProgress?.Invoke("历史消息已全部扫描完毕", false);
                            break;
                        }

                        emptyBatchCount = 0;
                        var coldItems = new List<HotMessageItem>();
                        long oldestInBatch = fromId;
                        long oldestDateInBatch = 0;

                        foreach (var msg in messages.MessagesValue)
                        {
                            oldestInBatch = msg.Id;
                            oldestDateInBatch = msg.Date;

                            var (count, emoji) = GetTopReaction(msg.InteractionInfo);
                            if (count > 0 && currentState.SampleCount < 50)
                            {
                                currentState.SampleCount++;
                                currentState.SampleSum += count;
                                if (currentState.SampleCount == 50)
                                {
                                    double avg = (double)currentState.SampleSum / currentState.SampleCount;
                                    currentState.ComputedThreshold = Math.Max(1, (int)Math.Floor(avg * 0.9));
                                    _database.PruneColdMessages(chatId, currentState.EffectiveThreshold, sevenDaysAgo);
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
                                    Snippet = snippet,
                                    SenderName = GetSenderDisplayName(clientService, msg.SenderId),
                                    HasMedia = hasMedia,
                                    IsCold = true
                                });
                            }
                        }

                        if (coldItems.Count > 0)
                        {
                            _database.UpsertMessages(chatId, coldItems);
                        }

                        // 防死循环：若 ID 未推进，说明到达顶端或无更多
                        if (oldestInBatch == fromId && fromId != 0)
                        {
                            currentState.ColdSyncCompleted = true;
                            _database.SaveSyncState(currentState);
                            onProgress?.Invoke("历史消息已全部扫描完毕", false);
                            break;
                        }

                        currentState.OldestSyncedMsgId = oldestInBatch;
                        _database.SaveSyncState(currentState);

                        int totalSaved = _database.GetTotalHotCount(chatId);
                        string dateStr = oldestDateInBatch > 0
                            ? DateTimeOffset.FromUnixTimeSeconds(oldestDateInBatch).ToLocalTime().ToString("yyyy/MM/dd")
                            : string.Empty;

                        onProgress?.Invoke($"慢爬中：已扫描至 {dateStr} (已收录 {totalSaved} 条)", true);

                        await Task.Delay(2500, token);
                    }
                }
                catch (OperationCanceledException)
                {
                    onProgress?.Invoke("慢爬已暂停", false);
                }
                catch (Exception ex)
                {
                    Logger.Exception(ex);
                    onProgress?.Invoke("慢爬发生异常", false);
                }
                finally
                {
                    _crawlers.TryRemove(chatId, out _);
                }
            }, cts.Token);
        }

        public void ResetChannelSync(long chatId)
        {
            StopColdCrawler(chatId);
            _database.ResetSyncState(chatId);
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
            var (count, emoji) = GetTopReaction(interactionInfo);
            var state = _database.GetSyncState(chatId);

            if (count >= state.EffectiveThreshold)
            {
                var existing = _database.GetTopMessages(chatId, 1, null, 1).FirstOrDefault(m => m.MessageId == messageId);
                if (existing != null)
                {
                    existing.MaxReactionCount = count;
                    existing.TopEmoji = emoji;
                    _database.UpsertMessages(chatId, new[] { existing });
                }
            }
            else
            {
                // If it fell below threshold and is cold, we can prune
                _database.PruneColdMessages(chatId, state.EffectiveThreshold, DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 7 * 86400);
            }
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
                try { kv.Value.Cancel(); kv.Value.Dispose(); } catch { }
            }
            _crawlers.Clear();
            _database?.Dispose();
        }
    }
}
