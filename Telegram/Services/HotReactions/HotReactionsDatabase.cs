//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using SQLitePCL;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Telegram.Common;
using Windows.Storage;

namespace Telegram.Services.HotReactions
{
    public class HotReactionsDatabase : IDisposable
    {
        private static readonly object _lock = new();
        private sqlite3 _db;

        static HotReactionsDatabase()
        {
            try
            {
                Batteries_V2.Init();
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to initialize sqlite3 provider: {ex}");
            }
        }

        public void Initialize()
        {
            lock (_lock)
            {
                if (_db != null)
                {
                    return;
                }

                try
                {
                    var dbPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "hot_reactions.db");
                    var result = raw.sqlite3_open(dbPath, out _db);
                    if (result != raw.SQLITE_OK)
                    {
                        Logger.Error($"Failed to open hot_reactions.db, code: {result}");
                        return;
                    }

                    CreateTables();

                    try
                    {
                        ExecuteNonQuery("ALTER TABLE channel_hot_messages ADD COLUMN reactions_json TEXT;");
                    }
                    catch { }

                    try
                    {
                        ExecuteNonQuery("ALTER TABLE channel_sync_state ADD COLUMN earliest_msg_id INTEGER DEFAULT 0;");
                    }
                    catch { }

                    try
                    {
                        ExecuteNonQuery("ALTER TABLE channel_sync_state ADD COLUMN earliest_msg_date INTEGER DEFAULT 0;");
                    }
                    catch { }

                    var configs = GetSentimentConfigs();
                    ReactionSentimentService.Current.LoadCustomConfig(configs);

                    // 一次性修复：解除此前版本因单批消息数 < 100 误判造成的 cold_sync_completed = 1 封锁
                    ExecuteNonQuery("UPDATE channel_sync_state SET cold_sync_completed = 0 WHERE cold_sync_completed = 1;");
                }
                catch (Exception ex)
                {
                    Logger.Exception(ex);
                }
            }
        }

        private void CreateTables()
        {
            ExecuteNonQuery(@"
                CREATE TABLE IF NOT EXISTS channel_sync_state (
                    chat_id INTEGER PRIMARY KEY,
                    newest_synced_msg_id INTEGER,
                    oldest_synced_msg_id INTEGER,
                    cold_sync_completed INTEGER DEFAULT 0,
                    sample_count INTEGER DEFAULT 0,
                    sample_sum INTEGER DEFAULT 0,
                    computed_threshold INTEGER DEFAULT 1,
                    custom_threshold INTEGER DEFAULT NULL,
                    last_hot_sync INTEGER DEFAULT 0,
                    earliest_msg_id INTEGER DEFAULT 0,
                    earliest_msg_date INTEGER DEFAULT 0
                );
            ");

            ExecuteNonQuery(@"
                CREATE TABLE IF NOT EXISTS channel_hot_messages (
                    chat_id INTEGER,
                    message_id INTEGER,
                    date INTEGER,
                    max_reaction_count INTEGER,
                    top_emoji TEXT,
                    snippet TEXT,
                    sender_name TEXT,
                    has_media INTEGER,
                    is_cold INTEGER DEFAULT 0,
                    is_deleted INTEGER DEFAULT 0,
                    reactions_json TEXT,
                    PRIMARY KEY (chat_id, message_id)
                );
            ");

            ExecuteNonQuery(@"
                CREATE TABLE IF NOT EXISTS reaction_sentiment_config (
                    emoji TEXT PRIMARY KEY,
                    category INTEGER
                );
            ");

            ExecuteNonQuery(@"
                CREATE INDEX IF NOT EXISTS idx_hot_rank 
                ON channel_hot_messages(chat_id, is_deleted, max_reaction_count DESC);
            ");

            ExecuteNonQuery(@"
                CREATE INDEX IF NOT EXISTS idx_hot_date 
                ON channel_hot_messages(chat_id, is_deleted, date DESC);
            ");
        }

        public ChannelSyncState GetSyncState(long chatId)
        {
            lock (_lock)
            {
                if (_db == null)
                {
                    return new ChannelSyncState { ChatId = chatId };
                }

                const string sql = @"
                    SELECT chat_id, newest_synced_msg_id, oldest_synced_msg_id, 
                           cold_sync_completed, sample_count, sample_sum, 
                           computed_threshold, custom_threshold, last_hot_sync,
                           earliest_msg_id, earliest_msg_date
                    FROM channel_sync_state WHERE chat_id = ?;
                ";

                sqlite3_stmt stmt = null;
                try
                {
                    if (raw.sqlite3_prepare_v2(_db, sql, out stmt) != raw.SQLITE_OK)
                    {
                        return new ChannelSyncState { ChatId = chatId };
                    }

                    raw.sqlite3_bind_int64(stmt, 1, chatId);

                    if (raw.sqlite3_step(stmt) == raw.SQLITE_ROW)
                    {
                        var state = new ChannelSyncState
                        {
                            ChatId = raw.sqlite3_column_int64(stmt, 0),
                            NewestSyncedMsgId = raw.sqlite3_column_int64(stmt, 1),
                            OldestSyncedMsgId = raw.sqlite3_column_int64(stmt, 2),
                            ColdSyncCompleted = raw.sqlite3_column_int(stmt, 3) != 0,
                            SampleCount = raw.sqlite3_column_int(stmt, 4),
                            SampleSum = raw.sqlite3_column_int(stmt, 5),
                            ComputedThreshold = Math.Max(1, raw.sqlite3_column_int(stmt, 6)),
                            CustomThreshold = raw.sqlite3_column_type(stmt, 7) == raw.SQLITE_NULL ? null : raw.sqlite3_column_int(stmt, 7),
                            LastHotSync = raw.sqlite3_column_int64(stmt, 8),
                            EarliestMsgId = raw.sqlite3_column_int64(stmt, 9),
                            EarliestMsgDate = raw.sqlite3_column_int64(stmt, 10)
                        };
                        return state;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Exception(ex);
                }
                finally
                {
                    if (stmt != null)
                    {
                        raw.sqlite3_finalize(stmt);
                    }
                }

                return new ChannelSyncState { ChatId = chatId };
            }
        }

        public void SaveSyncState(ChannelSyncState state)
        {
            lock (_lock)
            {
                if (_db == null || state == null)
                {
                    return;
                }

                const string sql = @"
                    INSERT OR REPLACE INTO channel_sync_state (
                        chat_id, newest_synced_msg_id, oldest_synced_msg_id,
                        cold_sync_completed, sample_count, sample_sum,
                        computed_threshold, custom_threshold, last_hot_sync,
                        earliest_msg_id, earliest_msg_date
                    ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);
                ";

                sqlite3_stmt stmt = null;
                try
                {
                    if (raw.sqlite3_prepare_v2(_db, sql, out stmt) != raw.SQLITE_OK)
                    {
                        return;
                    }

                    raw.sqlite3_bind_int64(stmt, 1, state.ChatId);
                    raw.sqlite3_bind_int64(stmt, 2, state.NewestSyncedMsgId);
                    raw.sqlite3_bind_int64(stmt, 3, state.OldestSyncedMsgId);
                    raw.sqlite3_bind_int(stmt, 4, state.ColdSyncCompleted ? 1 : 0);
                    raw.sqlite3_bind_int(stmt, 5, state.SampleCount);
                    raw.sqlite3_bind_int(stmt, 6, state.SampleSum);
                    raw.sqlite3_bind_int(stmt, 7, state.ComputedThreshold);

                    if (state.CustomThreshold.HasValue)
                    {
                        raw.sqlite3_bind_int(stmt, 8, state.CustomThreshold.Value);
                    }
                    else
                    {
                        raw.sqlite3_bind_null(stmt, 8);
                    }

                    raw.sqlite3_bind_int64(stmt, 9, state.LastHotSync);
                    raw.sqlite3_bind_int64(stmt, 10, state.EarliestMsgId);
                    raw.sqlite3_bind_int64(stmt, 11, state.EarliestMsgDate);

                    raw.sqlite3_step(stmt);
                }
                catch (Exception ex)
                {
                    Logger.Exception(ex);
                }
                finally
                {
                    if (stmt != null)
                    {
                        raw.sqlite3_finalize(stmt);
                    }
                }
            }
        }

        public void UpsertMessages(long chatId, IEnumerable<HotMessageItem> messages)
        {
            lock (_lock)
            {
                if (_db == null || messages == null)
                {
                    return;
                }

                ExecuteNonQuery("BEGIN TRANSACTION;");

                const string sql = @"
                    INSERT OR REPLACE INTO channel_hot_messages (
                        chat_id, message_id, date, max_reaction_count, top_emoji,
                        snippet, sender_name, has_media, is_cold, is_deleted, reactions_json
                    ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, 0, ?);
                ";

                sqlite3_stmt stmt = null;
                try
                {
                    if (raw.sqlite3_prepare_v2(_db, sql, out stmt) == raw.SQLITE_OK)
                    {
                        foreach (var msg in messages)
                        {
                            raw.sqlite3_bind_int64(stmt, 1, chatId);
                            raw.sqlite3_bind_int64(stmt, 2, msg.MessageId);
                            raw.sqlite3_bind_int64(stmt, 3, msg.Date);
                            raw.sqlite3_bind_int(stmt, 4, msg.MaxReactionCount);
                            raw.sqlite3_bind_text(stmt, 5, msg.TopEmoji ?? "👍");
                            raw.sqlite3_bind_text(stmt, 6, msg.Snippet ?? string.Empty);
                            raw.sqlite3_bind_text(stmt, 7, msg.SenderName ?? string.Empty);
                            raw.sqlite3_bind_int(stmt, 8, msg.HasMedia ? 1 : 0);
                            raw.sqlite3_bind_int(stmt, 9, msg.IsCold ? 1 : 0);
                            raw.sqlite3_bind_text(stmt, 10, msg.ReactionsJson ?? string.Empty);

                            raw.sqlite3_step(stmt);
                            raw.sqlite3_reset(stmt);
                        }
                    }

                    ExecuteNonQuery("COMMIT;");
                }
                catch (Exception ex)
                {
                    Logger.Exception(ex);
                    ExecuteNonQuery("ROLLBACK;");
                }
                finally
                {
                    if (stmt != null)
                    {
                        raw.sqlite3_finalize(stmt);
                    }
                }
            }
        }

        public void PruneColdMessages(long chatId, int threshold, long olderThanDate)
        {
            lock (_lock)
            {
                if (_db == null)
                {
                    return;
                }

                const string sql = @"
                    DELETE FROM channel_hot_messages 
                    WHERE chat_id = ? AND date < ? AND max_reaction_count < ?;
                ";

                sqlite3_stmt stmt = null;
                try
                {
                    if (raw.sqlite3_prepare_v2(_db, sql, out stmt) == raw.SQLITE_OK)
                    {
                        raw.sqlite3_bind_int64(stmt, 1, chatId);
                        raw.sqlite3_bind_int64(stmt, 2, olderThanDate);
                        raw.sqlite3_bind_int(stmt, 3, threshold);
                        raw.sqlite3_step(stmt);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Exception(ex);
                }
                finally
                {
                    if (stmt != null)
                    {
                        raw.sqlite3_finalize(stmt);
                    }
                }
            }
        }

        public void FreezeMessagesOlderThan(long chatId, long olderThanDate)
        {
            lock (_lock)
            {
                if (_db == null)
                {
                    return;
                }

                const string sql = @"
                    UPDATE channel_hot_messages 
                    SET is_cold = 1 
                    WHERE chat_id = ? AND date < ? AND is_cold = 0;
                ";

                sqlite3_stmt stmt = null;
                try
                {
                    if (raw.sqlite3_prepare_v2(_db, sql, out stmt) == raw.SQLITE_OK)
                    {
                        raw.sqlite3_bind_int64(stmt, 1, chatId);
                        raw.sqlite3_bind_int64(stmt, 2, olderThanDate);
                        raw.sqlite3_step(stmt);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Exception(ex);
                }
                finally
                {
                    if (stmt != null)
                    {
                        raw.sqlite3_finalize(stmt);
                    }
                }
            }
        }

        public List<HotMessageItem> GetTopMessages(long chatId, int limit = 0, long? minDate = null, int minReactions = 1, HotRankMode mode = HotRankMode.All)
        {
            lock (_lock)
            {
                var list = new List<HotMessageItem>();
                if (_db == null)
                {
                    return list;
                }

                // limit <= 0 时不加 SQL LIMIT，全量查出满足门槛的所有消息；若有 limit 且为 All 模式，直接在 SQL 限制
                bool hasLimit = limit > 0 && mode == HotRankMode.All;
                string limitClause = hasLimit ? " LIMIT ?" : string.Empty;

                var sql = minDate.HasValue
                    ? $@"SELECT chat_id, message_id, date, max_reaction_count, top_emoji, snippet, sender_name, has_media, is_cold, reactions_json
                        FROM channel_hot_messages 
                        WHERE chat_id = ? AND is_deleted = 0 AND max_reaction_count >= ? AND date >= ?
                        ORDER BY max_reaction_count DESC, date DESC{limitClause};"
                    : $@"SELECT chat_id, message_id, date, max_reaction_count, top_emoji, snippet, sender_name, has_media, is_cold, reactions_json
                        FROM channel_hot_messages 
                        WHERE chat_id = ? AND is_deleted = 0 AND max_reaction_count >= ?
                        ORDER BY max_reaction_count DESC, date DESC{limitClause};";

                sqlite3_stmt stmt = null;
                try
                {
                    if (raw.sqlite3_prepare_v2(_db, sql, out stmt) == raw.SQLITE_OK)
                    {
                        raw.sqlite3_bind_int64(stmt, 1, chatId);
                        raw.sqlite3_bind_int(stmt, 2, minReactions);

                        int paramIdx = 3;
                        if (minDate.HasValue)
                        {
                            raw.sqlite3_bind_int64(stmt, paramIdx++, minDate.Value);
                        }
                        if (hasLimit)
                        {
                            raw.sqlite3_bind_int(stmt, paramIdx++, limit);
                        }

                        while (raw.sqlite3_step(stmt) == raw.SQLITE_ROW)
                        {
                            list.Add(new HotMessageItem
                            {
                                ChatId = raw.sqlite3_column_int64(stmt, 0),
                                MessageId = raw.sqlite3_column_int64(stmt, 1),
                                Date = raw.sqlite3_column_int64(stmt, 2),
                                MaxReactionCount = raw.sqlite3_column_int(stmt, 3),
                                TopEmoji = raw.sqlite3_column_text(stmt, 4).utf8_to_string(),
                                Snippet = raw.sqlite3_column_text(stmt, 5).utf8_to_string(),
                                SenderName = raw.sqlite3_column_text(stmt, 6).utf8_to_string(),
                                HasMedia = raw.sqlite3_column_int(stmt, 7) != 0,
                                IsCold = raw.sqlite3_column_int(stmt, 8) != 0,
                                ReactionsJson = raw.sqlite3_column_type(stmt, 9) == raw.SQLITE_NULL ? null : raw.sqlite3_column_text(stmt, 9).utf8_to_string()
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Exception(ex);
                }
                finally
                {
                    if (stmt != null)
                    {
                        raw.sqlite3_finalize(stmt);
                    }
                }

                if (mode == HotRankMode.All)
                {
                    foreach (var item in list)
                    {
                        var (score, badge, sub) = ReactionSentimentService.Current.EvaluateMessage(item, mode);
                        item.DisplayScore = score;
                        item.DisplayBadge = badge;
                        item.SubDetailText = sub;
                    }
                    return limit > 0 ? list.Take(limit).ToList() : list;
                }

                var evaluated = new List<HotMessageItem>();
                foreach (var item in list)
                {
                    var (score, badge, sub) = ReactionSentimentService.Current.EvaluateMessage(item, mode);
                    if (score >= minReactions && score > 0)
                    {
                        item.DisplayScore = score;
                        item.DisplayBadge = badge;
                        item.SubDetailText = sub;
                        evaluated.Add(item);
                    }
                }

                var ordered = evaluated
                    .OrderByDescending(x => x.DisplayScore)
                    .ThenByDescending(x => x.Date);

                return limit > 0 ? ordered.Take(limit).ToList() : ordered.ToList();
            }
        }

        public Dictionary<string, SentimentCategory> GetSentimentConfigs()
        {
            lock (_lock)
            {
                var dict = new Dictionary<string, SentimentCategory>(StringComparer.Ordinal);
                if (_db == null) return dict;

                const string sql = "SELECT emoji, category FROM reaction_sentiment_config;";
                sqlite3_stmt stmt = null;
                try
                {
                    if (raw.sqlite3_prepare_v2(_db, sql, out stmt) == raw.SQLITE_OK)
                    {
                        while (raw.sqlite3_step(stmt) == raw.SQLITE_ROW)
                        {
                            var emoji = raw.sqlite3_column_text(stmt, 0).utf8_to_string();
                            var cat = (SentimentCategory)raw.sqlite3_column_int(stmt, 1);
                            if (!string.IsNullOrEmpty(emoji))
                            {
                                dict[emoji] = cat;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Exception(ex);
                }
                finally
                {
                    if (stmt != null) raw.sqlite3_finalize(stmt);
                }
                return dict;
            }
        }

        public void SaveSentimentConfig(string emoji, SentimentCategory? category)
        {
            lock (_lock)
            {
                if (_db == null || string.IsNullOrEmpty(emoji)) return;

                if (category.HasValue)
                {
                    const string sql = "INSERT OR REPLACE INTO reaction_sentiment_config (emoji, category) VALUES (?, ?);";
                    sqlite3_stmt stmt = null;
                    try
                    {
                        if (raw.sqlite3_prepare_v2(_db, sql, out stmt) == raw.SQLITE_OK)
                        {
                            raw.sqlite3_bind_text(stmt, 1, emoji);
                            raw.sqlite3_bind_int(stmt, 2, (int)category.Value);
                            raw.sqlite3_step(stmt);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Exception(ex);
                    }
                    finally
                    {
                        if (stmt != null) raw.sqlite3_finalize(stmt);
                    }
                }
                else
                {
                    const string sql = "DELETE FROM reaction_sentiment_config WHERE emoji = ?;";
                    sqlite3_stmt stmt = null;
                    try
                    {
                        if (raw.sqlite3_prepare_v2(_db, sql, out stmt) == raw.SQLITE_OK)
                        {
                            raw.sqlite3_bind_text(stmt, 1, emoji);
                            raw.sqlite3_step(stmt);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Exception(ex);
                    }
                    finally
                    {
                        if (stmt != null) raw.sqlite3_finalize(stmt);
                    }
                }
            }
        }

        public void ClearSentimentConfigs()
        {
            lock (_lock)
            {
                if (_db == null) return;
                ExecuteNonQuery("DELETE FROM reaction_sentiment_config;");
            }
        }

        public int GetTotalHotCount(long chatId)
        {
            lock (_lock)
            {
                if (_db == null)
                {
                    return 0;
                }

                const string sql = "SELECT COUNT(*) FROM channel_hot_messages WHERE chat_id = ? AND is_deleted = 0;";
                sqlite3_stmt stmt = null;
                try
                {
                    if (raw.sqlite3_prepare_v2(_db, sql, out stmt) == raw.SQLITE_OK)
                    {
                        raw.sqlite3_bind_int64(stmt, 1, chatId);
                        if (raw.sqlite3_step(stmt) == raw.SQLITE_ROW)
                        {
                            return raw.sqlite3_column_int(stmt, 0);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Exception(ex);
                }
                finally
                {
                    if (stmt != null)
                    {
                        raw.sqlite3_finalize(stmt);
                    }
                }

                return 0;
            }
        }

        public void ResetSyncState(long chatId)
        {
            lock (_lock)
            {
                if (_db == null)
                {
                    return;
                }

                const string sql1 = "DELETE FROM channel_sync_state WHERE chat_id = ?;";
                const string sql2 = "DELETE FROM channel_hot_messages WHERE chat_id = ?;";

                sqlite3_stmt stmt = null;
                try
                {
                    if (raw.sqlite3_prepare_v2(_db, sql1, out stmt) == raw.SQLITE_OK)
                    {
                        raw.sqlite3_bind_int64(stmt, 1, chatId);
                        raw.sqlite3_step(stmt);
                    }
                }
                finally
                {
                    if (stmt != null) raw.sqlite3_finalize(stmt);
                    stmt = null;
                }

                try
                {
                    if (raw.sqlite3_prepare_v2(_db, sql2, out stmt) == raw.SQLITE_OK)
                    {
                        raw.sqlite3_bind_int64(stmt, 1, chatId);
                        raw.sqlite3_step(stmt);
                    }
                }
                finally
                {
                    if (stmt != null) raw.sqlite3_finalize(stmt);
                }
            }
        }

        public void MarkMessageDeleted(long chatId, long messageId)
        {
            lock (_lock)
            {
                if (_db == null)
                {
                    return;
                }

                const string sql = "UPDATE channel_hot_messages SET is_deleted = 1 WHERE chat_id = ? AND message_id = ?;";
                sqlite3_stmt stmt = null;
                try
                {
                    if (raw.sqlite3_prepare_v2(_db, sql, out stmt) == raw.SQLITE_OK)
                    {
                        raw.sqlite3_bind_int64(stmt, 1, chatId);
                        raw.sqlite3_bind_int64(stmt, 2, messageId);
                        raw.sqlite3_step(stmt);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Exception(ex);
                }
                finally
                {
                    if (stmt != null)
                    {
                        raw.sqlite3_finalize(stmt);
                    }
                }
            }
        }

        private void ExecuteNonQuery(string sql)
        {
            if (_db == null)
            {
                return;
            }

            sqlite3_stmt stmt = null;
            try
            {
                if (raw.sqlite3_prepare_v2(_db, sql, out stmt) == raw.SQLITE_OK)
                {
                    raw.sqlite3_step(stmt);
                }
            }
            catch (Exception ex)
            {
                Logger.Exception(ex);
            }
            finally
            {
                if (stmt != null)
                {
                    raw.sqlite3_finalize(stmt);
                }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_db != null)
                {
                    raw.sqlite3_close(_db);
                    _db = null;
                }
            }
        }
    }
}
