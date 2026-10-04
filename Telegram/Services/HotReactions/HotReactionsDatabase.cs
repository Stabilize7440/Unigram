//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using SQLitePCL;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Windows.Storage;

namespace Telegram.Services.HotReactions
{
    public class HotReactionsDatabase : IDisposable
    {
        private readonly object _lock = new();
        private readonly string _path;
        private readonly ConcurrentDictionary<long, long> _revisions = new();
        private sqlite3 _db;
        private bool _initialized;
        private bool _disposed;
        private long _cachedChatId;
        private Dictionary<long, HotMessageItem> _cachedMessages;
        private List<HotMessageItem> _cachedRanking;
        private HotRankMode _cachedMode;
        private int _cachedThreshold;
        private long? _cachedMinDate;
        private int _cachedSentimentVersion;

        public ReactionSentimentService Sentiments { get; } = new();

        static HotReactionsDatabase()
        {
            Batteries_V2.Init();
        }

        public HotReactionsDatabase(int sessionId, long userId)
            : this(Path.Combine(ApplicationData.Current.LocalFolder.Path, sessionId.ToString(), $"hot_reactions_{userId}.db"))
        {
            if (userId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(userId));
            }
        }

        internal HotReactionsDatabase(string path)
        {
            _path = path;
        }

        public void Initialize()
        {
            lock (_lock)
            {
                ThrowIfDisposed();
                if (_initialized) return;

                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                var result = raw.sqlite3_open(_path, out _db);
                if (result != raw.SQLITE_OK)
                {
                    var error = raw.sqlite3_errmsg(_db).utf8_to_string();
                    raw.sqlite3_close(_db);
                    _db = null;
                    throw new InvalidOperationException($"Cannot open hot reactions database ({result}): {error}");
                }

                try
                {
                    ExecuteNonQuery("PRAGMA busy_timeout = 3000;");
                    ExecuteNonQuery("PRAGMA journal_mode = WAL;");
                    ExecuteNonQuery(@"CREATE TABLE IF NOT EXISTS channel_sync_state (
                        chat_id INTEGER PRIMARY KEY,
                        newest_synced_msg_id INTEGER DEFAULT 0,
                        oldest_synced_msg_id INTEGER DEFAULT 0,
                        cold_sync_completed INTEGER DEFAULT 0,
                        sample_count INTEGER DEFAULT 0,
                        sample_sum INTEGER DEFAULT 0,
                        computed_threshold INTEGER DEFAULT 1,
                        custom_threshold INTEGER,
                        last_hot_sync INTEGER DEFAULT 0,
                        earliest_msg_id INTEGER DEFAULT 0,
                        earliest_msg_date INTEGER DEFAULT 0,
                        pending_sync_from_id INTEGER DEFAULT 0,
                        pending_sync_newest_id INTEGER DEFAULT 0,
                        pending_sync_stop_id INTEGER DEFAULT 0,
                        indexed_threshold INTEGER DEFAULT 1
                    );");
                    ExecuteNonQuery(@"CREATE TABLE IF NOT EXISTS channel_hot_messages (
                        chat_id INTEGER,
                        message_id INTEGER,
                        date INTEGER DEFAULT 0,
                        max_reaction_count INTEGER DEFAULT 0,
                        top_emoji TEXT,
                        snippet TEXT,
                        sender_name TEXT,
                        has_media INTEGER DEFAULT 0,
                        is_cold INTEGER DEFAULT 0,
                        is_deleted INTEGER DEFAULT 0,
                        reactions_json TEXT,
                        PRIMARY KEY (chat_id, message_id)
                    );");
                    ExecuteNonQuery(@"CREATE TABLE IF NOT EXISTS channel_reaction_samples (
                        chat_id INTEGER, message_id INTEGER, reaction_count INTEGER,
                        PRIMARY KEY (chat_id, message_id)
                    );");
                    ExecuteNonQuery("CREATE TABLE IF NOT EXISTS reaction_sentiment_config (emoji TEXT PRIMARY KEY, category INTEGER);");
                    EnsureColumn("channel_hot_messages", "reactions_json", "TEXT");
                    EnsureColumn("channel_sync_state", "earliest_msg_id", "INTEGER DEFAULT 0");
                    EnsureColumn("channel_sync_state", "earliest_msg_date", "INTEGER DEFAULT 0");
                    EnsureColumn("channel_sync_state", "pending_sync_from_id", "INTEGER DEFAULT 0");
                    EnsureColumn("channel_sync_state", "pending_sync_newest_id", "INTEGER DEFAULT 0");
                    EnsureColumn("channel_sync_state", "pending_sync_stop_id", "INTEGER DEFAULT 0");
                    EnsureColumn("channel_sync_state", "indexed_threshold", "INTEGER DEFAULT 1");
                    ExecuteNonQuery(@"CREATE INDEX IF NOT EXISTS idx_hot_rank
                        ON channel_hot_messages(chat_id, is_deleted, max_reaction_count DESC, date DESC);");
                    ExecuteNonQuery(@"CREATE INDEX IF NOT EXISTS idx_hot_date
                        ON channel_hot_messages(chat_id, is_deleted, date DESC);");
                    ExecuteNonQuery("PRAGMA user_version = 1;");

                    using (var stmt = Prepare("SELECT chat_id FROM channel_sync_state;"))
                    {
                        while (Step(stmt) == raw.SQLITE_ROW)
                        {
                            _revisions.TryAdd(raw.sqlite3_column_int64(stmt, 0), 0);
                        }
                    }
                    Sentiments.LoadCustomConfig(ReadSentimentConfigs());
                    _initialized = true;
                }
                catch
                {
                    raw.sqlite3_close(_db);
                    _db = null;
                    throw;
                }
            }
        }

        public void EnsureInitialized()
        {
            Initialize();
        }

        public bool IsIndexed(long chatId) => _revisions.ContainsKey(chatId);
        public long GetRevision(long chatId) => _revisions.TryGetValue(chatId, out var revision) ? revision : 0;

        public ChannelSyncState GetSyncState(long chatId)
        {
            lock (_lock)
            {
                Initialize();
                using var stmt = Prepare(@"SELECT newest_synced_msg_id, oldest_synced_msg_id,
                    cold_sync_completed, sample_count, sample_sum, computed_threshold, custom_threshold,
                    last_hot_sync, earliest_msg_id, earliest_msg_date, pending_sync_from_id,
                    pending_sync_newest_id, pending_sync_stop_id, indexed_threshold
                    FROM channel_sync_state WHERE chat_id = ?;");
                raw.sqlite3_bind_int64(stmt, 1, chatId);
                if (Step(stmt) != raw.SQLITE_ROW) return new ChannelSyncState { ChatId = chatId };
                return new ChannelSyncState
                {
                    ChatId = chatId,
                    NewestSyncedMsgId = raw.sqlite3_column_int64(stmt, 0),
                    OldestSyncedMsgId = raw.sqlite3_column_int64(stmt, 1),
                    ColdSyncCompleted = raw.sqlite3_column_int(stmt, 2) != 0,
                    SampleCount = raw.sqlite3_column_int(stmt, 3),
                    SampleSum = raw.sqlite3_column_int64(stmt, 4),
                    ComputedThreshold = Math.Max(1, raw.sqlite3_column_int(stmt, 5)),
                    CustomThreshold = raw.sqlite3_column_type(stmt, 6) == raw.SQLITE_NULL ? null : raw.sqlite3_column_int(stmt, 6),
                    LastHotSync = raw.sqlite3_column_int64(stmt, 7),
                    EarliestMsgId = raw.sqlite3_column_int64(stmt, 8),
                    EarliestMsgDate = raw.sqlite3_column_int64(stmt, 9),
                    PendingSyncFromId = raw.sqlite3_column_int64(stmt, 10),
                    PendingSyncNewestId = raw.sqlite3_column_int64(stmt, 11),
                    PendingSyncStopId = raw.sqlite3_column_int64(stmt, 12),
                    IndexedThreshold = Math.Max(1, raw.sqlite3_column_int(stmt, 13))
                };
            }
        }

        // Callers update only their fields on the current row, inside the same transaction as the batch.
        public ChannelSyncState CommitBatch(long chatId, IReadOnlyList<HotMessageItem> messages,
            Action<ChannelSyncState> advance, long coldCutoff)
        {
            lock (_lock)
            {
                Initialize();
                ChannelSyncState state = null;
                int oldThreshold = 0;
                var saved = new List<HotMessageItem>();
                bool pruned = false;
                RunInTransaction(() =>
                {
                    state = GetSyncState(chatId);
                    oldThreshold = state.EffectiveThreshold;
                    using var sample = Prepare(@"INSERT OR IGNORE INTO channel_reaction_samples
                        (chat_id, message_id, reaction_count) SELECT ?, ?, ?
                        WHERE NOT EXISTS (SELECT 1 FROM channel_hot_messages
                            WHERE chat_id = ? AND message_id = ? AND is_deleted = 1);");
                    foreach (var item in messages)
                    {
                        if (item.MaxReactionCount <= 0 || state.SampleCount >= 50) continue;
                        raw.sqlite3_bind_int64(sample, 1, chatId);
                        raw.sqlite3_bind_int64(sample, 2, item.MessageId);
                        raw.sqlite3_bind_int(sample, 3, item.MaxReactionCount);
                        raw.sqlite3_bind_int64(sample, 4, chatId);
                        raw.sqlite3_bind_int64(sample, 5, item.MessageId);
                        Step(sample);
                        if (raw.sqlite3_changes(_db) != 0)
                        {
                            state.SampleCount++;
                            state.SampleSum += item.MaxReactionCount;
                        }
                        Check(raw.sqlite3_reset(sample));
                    }
                    if (state.SampleCount == 50)
                    {
                        state.ComputedThreshold = ComputeThreshold(state);
                    }

                    advance?.Invoke(state);
                    if (state.ColdSyncCompleted && state.SampleCount > 0 && state.SampleCount < 50)
                    {
                        state.ComputedThreshold = ComputeThreshold(state);
                    }
                    using var upsert = Prepare(UpsertSql);
                    foreach (var item in messages)
                    {
                        if (item.MaxReactionCount <= 0)
                        {
                            if (ClearReactionCount(chatId, item.MessageId)) saved.Add(new HotMessageItem { MessageId = item.MessageId });
                        }
                        else if (item.Date < coldCutoff && item.MaxReactionCount < state.EffectiveThreshold)
                        {
                            if (DeleteVisibleMessage(chatId, item.MessageId)) saved.Add(new HotMessageItem { MessageId = item.MessageId });
                        }
                        else if (WriteMessage(upsert, chatId, item))
                        {
                            saved.Add(item);
                        }
                    }
                    if (messages.Any(x => x.Date < coldCutoff))
                    {
                        state.IndexedThreshold = Math.Max(state.IndexedThreshold, state.EffectiveThreshold);
                    }
                    // A metadata checkpoint also prunes rows that have aged out of the hot window.
                    if (state.EffectiveThreshold != oldThreshold || messages.Count == 0)
                    {
                        Prune(chatId, state.EffectiveThreshold, coldCutoff);
                        pruned = raw.sqlite3_changes(_db) != 0;
                        state.IndexedThreshold = Math.Max(state.IndexedThreshold, state.EffectiveThreshold);
                    }
                    SaveState(state);
                });
                _revisions.TryAdd(chatId, 0);
                UpdateCachedMessages(chatId, saved);
                if (pruned || state.EffectiveThreshold != oldThreshold) PruneCache(chatId, state.EffectiveThreshold, coldCutoff);
                if (pruned || saved.Count > 0 || state.EffectiveThreshold != oldThreshold) Changed(chatId);
                return state;
            }
        }

        private static int ComputeThreshold(ChannelSyncState state)
        {
            return Math.Max(1, (int)Math.Floor((double)state.SampleSum / state.SampleCount * 0.9));
        }

        private void SaveState(ChannelSyncState state)
        {
            using var stmt = Prepare(@"INSERT OR REPLACE INTO channel_sync_state
                (chat_id, newest_synced_msg_id, oldest_synced_msg_id, cold_sync_completed,
                 sample_count, sample_sum, computed_threshold, custom_threshold, last_hot_sync,
                 earliest_msg_id, earliest_msg_date, pending_sync_from_id, pending_sync_newest_id,
                 pending_sync_stop_id, indexed_threshold) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);");
            raw.sqlite3_bind_int64(stmt, 1, state.ChatId);
            raw.sqlite3_bind_int64(stmt, 2, state.NewestSyncedMsgId);
            raw.sqlite3_bind_int64(stmt, 3, state.OldestSyncedMsgId);
            raw.sqlite3_bind_int(stmt, 4, state.ColdSyncCompleted ? 1 : 0);
            raw.sqlite3_bind_int(stmt, 5, state.SampleCount);
            raw.sqlite3_bind_int64(stmt, 6, state.SampleSum);
            raw.sqlite3_bind_int(stmt, 7, state.ComputedThreshold);
            if (state.CustomThreshold.HasValue) raw.sqlite3_bind_int(stmt, 8, state.CustomThreshold.Value);
            else raw.sqlite3_bind_null(stmt, 8);
            raw.sqlite3_bind_int64(stmt, 9, state.LastHotSync);
            raw.sqlite3_bind_int64(stmt, 10, state.EarliestMsgId);
            raw.sqlite3_bind_int64(stmt, 11, state.EarliestMsgDate);
            raw.sqlite3_bind_int64(stmt, 12, state.PendingSyncFromId);
            raw.sqlite3_bind_int64(stmt, 13, state.PendingSyncNewestId);
            raw.sqlite3_bind_int64(stmt, 14, state.PendingSyncStopId);
            raw.sqlite3_bind_int(stmt, 15, state.IndexedThreshold);
            Step(stmt);
        }

        private const string UpsertSql = @"INSERT OR REPLACE INTO channel_hot_messages
            (chat_id, message_id, date, max_reaction_count, top_emoji, snippet, sender_name,
             has_media, is_cold, is_deleted, reactions_json)
            SELECT ?, ?, ?, ?, ?, ?, ?, ?, ?, 0, ?
            WHERE NOT EXISTS (SELECT 1 FROM channel_hot_messages
                WHERE chat_id = ? AND message_id = ? AND is_deleted = 1);";

        private bool WriteMessage(sqlite3_stmt stmt, long chatId, HotMessageItem item)
        {
            raw.sqlite3_bind_int64(stmt, 1, chatId);
            raw.sqlite3_bind_int64(stmt, 2, item.MessageId);
            raw.sqlite3_bind_int64(stmt, 3, item.Date);
            raw.sqlite3_bind_int(stmt, 4, item.MaxReactionCount);
            raw.sqlite3_bind_text(stmt, 5, item.TopEmoji ?? string.Empty);
            raw.sqlite3_bind_text(stmt, 6, item.Snippet ?? string.Empty);
            raw.sqlite3_bind_text(stmt, 7, item.SenderName ?? string.Empty);
            raw.sqlite3_bind_int(stmt, 8, item.HasMedia ? 1 : 0);
            raw.sqlite3_bind_int(stmt, 9, item.IsCold ? 1 : 0);
            raw.sqlite3_bind_text(stmt, 10, item.ReactionsJson ?? string.Empty);
            raw.sqlite3_bind_int64(stmt, 11, chatId);
            raw.sqlite3_bind_int64(stmt, 12, item.MessageId);
            Step(stmt);
            var changed = raw.sqlite3_changes(_db) != 0;
            Check(raw.sqlite3_reset(stmt));
            return changed;
        }

        public void UpsertMessages(long chatId, IEnumerable<HotMessageItem> messages)
        {
            lock (_lock)
            {
                Initialize();
                var saved = new List<HotMessageItem>();
                RunInTransaction(() =>
                {
                    using var stmt = Prepare(UpsertSql);
                    foreach (var item in messages)
                    {
                        if (WriteMessage(stmt, chatId, item)) saved.Add(item);
                    }
                });
                UpdateCachedMessages(chatId, saved);
                if (saved.Count > 0) Changed(chatId);
            }
        }

        public bool RequiresRescan(long chatId, int? threshold)
        {
            var state = GetSyncState(chatId);
            return (threshold ?? state.ComputedThreshold) < state.IndexedThreshold;
        }

        public bool SetCustomThreshold(long chatId, int? threshold, bool rescanConfirmed = false)
        {
            if (threshold < 1) throw new ArgumentOutOfRangeException(nameof(threshold));
            lock (_lock)
            {
                Initialize();
                ChannelSyncState state = null;
                bool applied = false;
                var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 7 * 86400;
                RunInTransaction(() =>
                {
                    state = GetSyncState(chatId);
                    if (!rescanConfirmed && (threshold ?? state.ComputedThreshold) < state.IndexedThreshold) return;
                    applied = true;
                    state.CustomThreshold = threshold;
                    Prune(chatId, state.EffectiveThreshold, cutoff);
                    state.IndexedThreshold = Math.Max(state.IndexedThreshold, state.EffectiveThreshold);
                    SaveState(state);
                });
                if (!applied) return false;
                PruneCache(chatId, state.EffectiveThreshold, cutoff);
                Changed(chatId);
                return true;
            }
        }

        private void Prune(long chatId, int threshold, long cutoff)
        {
            using var stmt = Prepare(@"DELETE FROM channel_hot_messages
                WHERE chat_id = ? AND is_deleted = 0 AND date < ? AND max_reaction_count < ?;");
            raw.sqlite3_bind_int64(stmt, 1, chatId);
            raw.sqlite3_bind_int64(stmt, 2, cutoff);
            raw.sqlite3_bind_int(stmt, 3, threshold);
            Step(stmt);
        }

        private bool ClearReactionCount(long chatId, long messageId)
        {
            using var stmt = Prepare(@"UPDATE channel_hot_messages SET max_reaction_count = 0,
                top_emoji = '', reactions_json = '' WHERE chat_id = ? AND message_id = ?
                AND is_deleted = 0 AND max_reaction_count > 0;");
            raw.sqlite3_bind_int64(stmt, 1, chatId);
            raw.sqlite3_bind_int64(stmt, 2, messageId);
            Step(stmt);
            return raw.sqlite3_changes(_db) != 0;
        }

        private bool DeleteVisibleMessage(long chatId, long messageId)
        {
            using var stmt = Prepare("DELETE FROM channel_hot_messages WHERE chat_id = ? AND message_id = ? AND is_deleted = 0;");
            raw.sqlite3_bind_int64(stmt, 1, chatId);
            raw.sqlite3_bind_int64(stmt, 2, messageId);
            Step(stmt);
            return raw.sqlite3_changes(_db) != 0;
        }

        public List<HotMessageItem> GetTopMessages(long chatId, int limit = 0, long? minDate = null,
            int minReactions = 1, HotRankMode mode = HotRankMode.All, CancellationToken token = default)
        {
            lock (_lock)
            {
                Initialize();
                token.ThrowIfCancellationRequested();
                EnsureMessageCache(chatId, token);
                var version = Sentiments.Version;
                if (_cachedRanking == null || _cachedMode != mode || _cachedThreshold != minReactions
                    || _cachedMinDate != minDate || _cachedSentimentVersion != version)
                {
                    var scored = new List<HotMessageItem>();
                    foreach (var item in _cachedMessages.Values)
                    {
                        token.ThrowIfCancellationRequested();
                        if (item.MaxReactionCount < minReactions || (minDate.HasValue && item.Date < minDate)) continue;
                        var display = item.GetDisplay(mode, Sentiments);
                        if (display == null || display.DisplayScore < minReactions) continue;
                        scored.Add(display);
                    }
                    token.ThrowIfCancellationRequested();
                    scored.Sort((a, b) =>
                    {
                        var result = b.DisplayScore.CompareTo(a.DisplayScore);
                        if (result == 0) result = b.Date.CompareTo(a.Date);
                        return result != 0 ? result : b.MessageId.CompareTo(a.MessageId);
                    });
                    _cachedRanking = scored;
                    _cachedMode = mode;
                    _cachedThreshold = minReactions;
                    _cachedMinDate = minDate;
                    _cachedSentimentVersion = version;
                }
                return limit > 0 ? _cachedRanking.Take(limit).ToList() : new List<HotMessageItem>(_cachedRanking);
            }
        }

        private void EnsureMessageCache(long chatId, CancellationToken token)
        {
            if (_cachedChatId == chatId && _cachedMessages != null) return;
            var items = new Dictionary<long, HotMessageItem>();
            using var stmt = Prepare(@"SELECT message_id, date, max_reaction_count, top_emoji,
                snippet, sender_name, has_media, is_cold, reactions_json FROM channel_hot_messages
                WHERE chat_id = ? AND is_deleted = 0 AND max_reaction_count > 0;");
            raw.sqlite3_bind_int64(stmt, 1, chatId);
            while (Step(stmt) == raw.SQLITE_ROW)
            {
                token.ThrowIfCancellationRequested();
                var item = new HotMessageItem
                {
                    ChatId = chatId,
                    MessageId = raw.sqlite3_column_int64(stmt, 0),
                    Date = raw.sqlite3_column_int64(stmt, 1),
                    MaxReactionCount = raw.sqlite3_column_int(stmt, 2),
                    TopEmoji = ReadText(stmt, 3),
                    Snippet = ReadText(stmt, 4),
                    SenderName = ReadText(stmt, 5),
                    HasMedia = raw.sqlite3_column_int(stmt, 6) != 0,
                    IsCold = raw.sqlite3_column_int(stmt, 7) != 0,
                    ReactionsJson = ReadText(stmt, 8)
                };
                items.Add(item.MessageId, item);
            }
            _cachedChatId = chatId;
            _cachedMessages = items;
            _cachedRanking = null;
        }

        private void UpdateCachedMessages(long chatId, IEnumerable<HotMessageItem> items)
        {
            if (_cachedChatId != chatId || _cachedMessages == null) return;
            foreach (var item in items)
            {
                if (item.MaxReactionCount <= 0) _cachedMessages.Remove(item.MessageId);
                else _cachedMessages[item.MessageId] = item;
            }
            _cachedRanking = null;
        }

        private void PruneCache(long chatId, int threshold, long cutoff)
        {
            if (_cachedChatId != chatId || _cachedMessages == null) return;
            foreach (var key in _cachedMessages.Where(x => x.Value.Date < cutoff && x.Value.MaxReactionCount < threshold).Select(x => x.Key).ToArray())
            {
                _cachedMessages.Remove(key);
            }
            _cachedRanking = null;
        }

        public void ApplyUpdates(long chatId, IReadOnlyList<long> deleted,
            IReadOnlyList<(long MessageId, int Count, string Emoji, string Json)> reactions)
        {
            lock (_lock)
            {
                Initialize();
                var changed = new List<long>();
                RunInTransaction(() =>
                {
                    foreach (var messageId in deleted)
                    {
                        using var stmt = Prepare(@"INSERT OR REPLACE INTO channel_hot_messages
                            (chat_id, message_id, is_deleted) VALUES (?, ?, 1);");
                        raw.sqlite3_bind_int64(stmt, 1, chatId);
                        raw.sqlite3_bind_int64(stmt, 2, messageId);
                        Step(stmt);
                        changed.Add(messageId);
                    }
                    foreach (var item in reactions)
                    {
                        using var stmt = Prepare(@"UPDATE channel_hot_messages
                            SET max_reaction_count = ?, top_emoji = ?, reactions_json = ?
                            WHERE chat_id = ? AND message_id = ? AND is_deleted = 0
                            AND (max_reaction_count != ? OR COALESCE(top_emoji, '') != ? OR COALESCE(reactions_json, '') != ?);");
                        raw.sqlite3_bind_int(stmt, 1, item.Count);
                        raw.sqlite3_bind_text(stmt, 2, item.Emoji ?? string.Empty);
                        raw.sqlite3_bind_text(stmt, 3, item.Json ?? string.Empty);
                        raw.sqlite3_bind_int64(stmt, 4, chatId);
                        raw.sqlite3_bind_int64(stmt, 5, item.MessageId);
                        raw.sqlite3_bind_int(stmt, 6, item.Count);
                        raw.sqlite3_bind_text(stmt, 7, item.Emoji ?? string.Empty);
                        raw.sqlite3_bind_text(stmt, 8, item.Json ?? string.Empty);
                        Step(stmt);
                        if (raw.sqlite3_changes(_db) != 0) changed.Add(item.MessageId);
                    }
                });
                if (_cachedChatId == chatId && _cachedMessages != null)
                {
                    foreach (var messageId in deleted) _cachedMessages.Remove(messageId);
                    foreach (var item in reactions)
                    {
                        if (!_cachedMessages.TryGetValue(item.MessageId, out var cached))
                        {
                            if (item.Count > 0 && changed.Contains(item.MessageId)) _cachedMessages = null;
                            if (_cachedMessages == null) break;
                            continue;
                        }
                        if (item.Count <= 0) _cachedMessages.Remove(item.MessageId);
                        else
                        {
                            cached.MaxReactionCount = item.Count;
                            cached.TopEmoji = item.Emoji;
                            cached.ReactionsJson = item.Json;
                        }
                    }
                }
                if (changed.Count > 0) Changed(chatId);
            }
        }

        public int GetTotalHotCount(long chatId)
        {
            lock (_lock)
            {
                Initialize();
                using var stmt = Prepare("SELECT COUNT(*) FROM channel_hot_messages WHERE chat_id = ? AND is_deleted = 0 AND max_reaction_count > 0;");
                raw.sqlite3_bind_int64(stmt, 1, chatId);
                Step(stmt);
                return raw.sqlite3_column_int(stmt, 0);
            }
        }

        public void ResetSyncState(long chatId)
        {
            lock (_lock)
            {
                Initialize();
                RunInTransaction(() =>
                {
                    var state = new ChannelSyncState { ChatId = chatId, CustomThreshold = GetSyncState(chatId).CustomThreshold };
                    using var messages = Prepare("DELETE FROM channel_hot_messages WHERE chat_id = ? AND is_deleted = 0;");
                    raw.sqlite3_bind_int64(messages, 1, chatId);
                    Step(messages);
                    using var samples = Prepare("DELETE FROM channel_reaction_samples WHERE chat_id = ?;");
                    raw.sqlite3_bind_int64(samples, 1, chatId);
                    Step(samples);
                    SaveState(state);
                });
                if (_cachedChatId == chatId)
                {
                    _cachedMessages?.Clear();
                }
                Changed(chatId);
            }
        }

        public Dictionary<string, SentimentCategory> GetSentimentConfigs()
        {
            lock (_lock)
            {
                Initialize();
                return ReadSentimentConfigs();
            }
        }

        private Dictionary<string, SentimentCategory> ReadSentimentConfigs()
        {
            var result = new Dictionary<string, SentimentCategory>(StringComparer.Ordinal);
            using var stmt = Prepare("SELECT emoji, category FROM reaction_sentiment_config;");
            while (Step(stmt) == raw.SQLITE_ROW)
            {
                result[ReadText(stmt, 0)] = (SentimentCategory)raw.sqlite3_column_int(stmt, 1);
            }
            return result;
        }

        public void SaveSentimentConfig(string emoji, SentimentCategory? category)
        {
            lock (_lock)
            {
                Initialize();
                using var stmt = Prepare(category.HasValue
                    ? "INSERT OR REPLACE INTO reaction_sentiment_config (emoji, category) VALUES (?, ?);"
                    : "DELETE FROM reaction_sentiment_config WHERE emoji = ?;");
                raw.sqlite3_bind_text(stmt, 1, emoji);
                if (category.HasValue) raw.sqlite3_bind_int(stmt, 2, (int)category.Value);
                Step(stmt);
                Sentiments.SetCustomCategory(emoji, category);
                _cachedRanking = null;
            }
        }

        public void ClearSentimentConfigs()
        {
            lock (_lock)
            {
                Initialize();
                ExecuteNonQuery("DELETE FROM reaction_sentiment_config;");
                Sentiments.ResetToDefaults();
                _cachedRanking = null;
            }
        }

        private void Changed(long chatId)
        {
            _revisions.AddOrUpdate(chatId, 1, (_, revision) => revision + 1);
            if (_cachedChatId == chatId) _cachedRanking = null;
        }

        private void EnsureColumn(string table, string column, string declaration)
        {
            using (var stmt = Prepare($"PRAGMA table_info({table});"))
            {
                while (Step(stmt) == raw.SQLITE_ROW)
                {
                    if (ReadText(stmt, 1) == column) return;
                }
            }
            ExecuteNonQuery($"ALTER TABLE {table} ADD COLUMN {column} {declaration};");
        }

        private sqlite3_stmt Prepare(string sql)
        {
            var result = raw.sqlite3_prepare_v2(_db, sql, out var stmt);
            if (result != raw.SQLITE_OK) stmt?.Dispose();
            Check(result);
            return stmt;
        }

        private int Step(sqlite3_stmt stmt)
        {
            var result = raw.sqlite3_step(stmt);
            Check(result);
            return result;
        }

        private void Check(int result)
        {
            if (result != raw.SQLITE_OK && result != raw.SQLITE_ROW && result != raw.SQLITE_DONE)
            {
                throw new InvalidOperationException($"Hot reactions SQLite error ({result}): {raw.sqlite3_errmsg(_db).utf8_to_string()}");
            }
        }

        private void ExecuteNonQuery(string sql)
        {
            using var stmt = Prepare(sql);
            Step(stmt);
        }

        private void RunInTransaction(Action action)
        {
            ExecuteNonQuery("BEGIN IMMEDIATE;");
            try
            {
                action();
                ExecuteNonQuery("COMMIT;");
            }
            catch
            {
                try { ExecuteNonQuery("ROLLBACK;"); }
                catch (Exception ex) { Logger.Exception(ex); }
                throw;
            }
        }

        private static string ReadText(sqlite3_stmt stmt, int column)
        {
            return raw.sqlite3_column_type(stmt, column) == raw.SQLITE_NULL ? string.Empty : raw.sqlite3_column_text(stmt, column).utf8_to_string();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(HotReactionsDatabase));
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                _cachedMessages = null;
                _cachedRanking = null;
                if (_db != null)
                {
                    raw.sqlite3_close(_db);
                    _db = null;
                }
            }
        }
    }
}
