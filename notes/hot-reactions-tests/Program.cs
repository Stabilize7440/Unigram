using System.Diagnostics;
using System.Text.Json;
using SQLitePCL;
using Telegram.Services;
using Telegram.Services.HotReactions;
using Telegram.Td.Api;

class Program
{
    private static readonly long Now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static readonly long Cutoff = Now - 7 * 86400;
    private static readonly long Old = Now - 10 * 86400;
    private static int _assertions;

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL " + name);
        _assertions++;
        Console.WriteLine("PASS " + name);
    }

    private static long Id(int value) => (long)value << 20;
    private static string PathFor(string name) => Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, name + ".db");
    private static HotReactionsDatabase Database(string name) => new(PathFor(name));
    private static void Seed(HotReactionsDatabase db, long chat, Action<ChannelSyncState> update) => db.CommitBatch(chat, Array.Empty<HotMessageItem>(), update, Cutoff);
    private static TaskCompletionSource<T> Source<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static MessageInteractionInfo Info(int count, string emoji = "👍") => new()
    {
        Reactions = new() { Reactions = new() { new() { Type = new ReactionTypeEmoji { Emoji = emoji }, TotalCount = count } } }
    };
    private static Message Msg(int id, int count = 10, long? date = null) => new()
    {
        Id = Id(id), Date = date ?? Now, InteractionInfo = Info(count)
    };
    private static Messages Batch(params Message[] messages) => new() { MessagesValue = messages.ToList() };
    private static HotMessageItem Item(long chat, int id, int count = 10, long? date = null) => new()
    {
        ChatId = chat, MessageId = Id(id), Date = date ?? Old, MaxReactionCount = count,
        TopEmoji = "👍", Snippet = "test", SenderName = "test", IsCold = (date ?? Old) < Cutoff,
        ReactionsJson = JsonSerializer.Serialize(new Dictionary<string, int> { ["👍"] = count, ["👎"] = 1 })
    };
    private static FakeClient History(IEnumerable<Message> messages)
    {
        var source = messages.OrderByDescending(x => x.Id).ToList();
        return new FakeClient
        {
            Handler = q => Task.FromResult<Object>(q.Offset < 0
                ? Batch(source.Last())
                : new Messages { MessagesValue = source.Where(x => q.FromMessageId == 0 || x.Id < q.FromMessageId).Take(q.Limit).ToList() })
        };
    }
    private static HotReactionsService Service(FakeClient client, HotReactionsDatabase db, int intervalMs = 0) => new(client, db, TimeSpan.FromMilliseconds(intervalMs));
    private static async Task Until(Func<bool> condition, string name, int timeoutMs = 8000)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.ElapsedMilliseconds > timeoutMs) throw new TimeoutException(name);
            await Task.Delay(10);
        }
    }
    private static async Task Scan(HotReactionsService service, FakeClient client, long chat)
    {
        var done = Source<string>();
        service.StartColdCrawler(client, chat, (status, running) => { if (!running) done.TrySetResult(status); });
        var status = await done.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Check(status.Contains("全部扫描完毕"), $"chat {chat} finishes with an ID boundary");
        await Until(() => !service.IsCrawlerRunning(chat), "crawler cleanup");
    }
    private static void Sql(string path, string command)
    {
        raw.sqlite3_open(path, out var db);
        try
        {
            int result = raw.sqlite3_exec(db, command);
            if (result != raw.SQLITE_OK) throw new Exception(raw.sqlite3_errmsg(db).utf8_to_string());
        }
        finally { raw.sqlite3_close(db); }
    }

    private static void DatabaseTests()
    {
        using var a = new HotReactionsDatabase(1, 111);
        using var b = new HotReactionsDatabase(1, 222);
        a.Initialize(); b.Initialize();
        a.CommitBatch(1, new[] { Item(1, 1, 20) }, state => state.OldestSyncedMsgId = Id(1), Cutoff);
        a.SaveSentimentConfig("👍", SentimentCategory.Negative);
        Check(b.GetTotalHotCount(1) == 0, "F01 same chat is isolated by account, even when a session slot is reused");
        Check(b.Sentiments.GetCategory("👍") == SentimentCategory.Positive, "F01 sentiment configuration is account-local");
        using (var legacy = new HotReactionsDatabase(Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "hot_reactions.db")))
        {
            legacy.Initialize(); legacy.UpsertMessages(2, new[] { Item(2, 1) });
            Check(a.GetTotalHotCount(2) == 0 && legacy.GetTotalHotCount(2) == 1, "F01 unscoped legacy database is preserved and not imported");
        }

        using var db = Database("atomic");
        db.Initialize();
        Sql(PathFor("atomic"), "CREATE TRIGGER fail_message BEFORE INSERT ON channel_hot_messages BEGIN SELECT RAISE(ABORT,'injected'); END;");
        bool threw = false;
        try { db.CommitBatch(3, new[] { Item(3, 1) }, state => state.OldestSyncedMsgId = Id(1), Cutoff); }
        catch (InvalidOperationException) { threw = true; }
        Check(threw && db.GetTotalHotCount(3) == 0 && db.GetSyncState(3).OldestSyncedMsgId == 0 && db.GetSyncState(3).SampleCount == 0, "F02 message write error rolls back rows, samples and cursor");
        Sql(PathFor("atomic"), "DROP TRIGGER fail_message; CREATE TRIGGER fail_state BEFORE INSERT ON channel_sync_state BEGIN SELECT RAISE(ABORT,'injected state'); END;");
        threw = false;
        try { db.CommitBatch(3, new[] { Item(3, 1) }, state => state.OldestSyncedMsgId = Id(1), Cutoff); }
        catch (InvalidOperationException) { threw = true; }
        Check(threw && db.GetTotalHotCount(3) == 0, "F02 state write error also rolls back the message batch");
        Sql(PathFor("atomic"), "DROP TRIGGER fail_state;");
        db.CommitBatch(3, new[] { Item(3, 1) }, state => state.OldestSyncedMsgId = Id(1), Cutoff);
        Check(db.GetSyncState(3).SampleCount == 1, "F02 retry after rollback samples the message exactly once");
        var revision = db.GetRevision(3);
        db.CommitBatch(3, Array.Empty<HotMessageItem>(), state => state.EarliestMsgId = Id(1), Cutoff);
        Check(db.GetRevision(3) == revision, "F11 metadata-only progress does not invalidate ranking data");
        db.CommitBatch(3, Array.Empty<HotMessageItem>(), state => state.ColdSyncCompleted = true, Cutoff);
        using (var reopened = Database("atomic"))
        {
            reopened.Initialize();
            Check(reopened.GetSyncState(3).ColdSyncCompleted, "F10 database initialization preserves completed state");
        }

        using var deletions = Database("deletions");
        deletions.Initialize();
        deletions.UpsertMessages(4, new[] { Item(4, 1) });
        deletions.ApplyUpdates(4, new[] { Id(1), Id(2) }, Array.Empty<(long, int, string, string)>());
        deletions.UpsertMessages(4, new[] { Item(4, 1), Item(4, 2) });
        Check(deletions.GetTotalHotCount(4) == 0, "F08 stale history cannot resurrect known or not-yet-indexed deleted messages");
        deletions.ResetSyncState(4);
        deletions.UpsertMessages(4, new[] { Item(4, 1), Item(4, 2) });
        Check(deletions.GetTotalHotCount(4) == 0, "F08 rescan retains authoritative deletion tombstones");

        using var threshold = Database("threshold");
        threshold.Initialize();
        threshold.UpsertMessages(5, new[] { Item(5, 1, 5), Item(5, 2, 20) });
        threshold.SetCustomThreshold(5, 10);
        Check(threshold.RequiresRescan(5, 1) && threshold.GetTotalHotCount(5) == 1, "F09 lowering below indexed floor requires confirmation and rescan");
        Check(threshold.GetSyncState(5).CustomThreshold == 10, "F09 detecting a required rescan does not apply an unconfirmed threshold");
        Check(!threshold.SetCustomThreshold(5, 1) && threshold.GetSyncState(5).CustomThreshold == 10, "F09 atomic floor recheck rejects an unconfirmed lower threshold");
        threshold.SetCustomThreshold(5, 1, rescanConfirmed: true);
        threshold.ResetSyncState(5);
        Check(threshold.GetSyncState(5).CustomThreshold == 1 && !threshold.RequiresRescan(5, 1), "F09 confirmed rescan preserves the new threshold and resets index floor");
        threshold.CommitBatch(5, new[] { Item(5, 1, 5), Item(5, 2, 20) }, state => state.ColdSyncCompleted = true, Cutoff);
        Check(threshold.GetTotalHotCount(5) == 2, "F09 lower-threshold rescan restores previously pruned messages");
        using var race = Database("threshold-race");
        race.Initialize();
        Check(!race.RequiresRescan(7, 1), "F09 fresh index initially has no pruning floor");
        race.CommitBatch(7, new[] { Item(7, 1) }, state => state.ColdSyncCompleted = true, Cutoff);
        Check(!race.SetCustomThreshold(7, 1) && race.GetSyncState(7).CustomThreshold == null, "F09 floor raised after UI precheck still requires confirmation");
        using var samples = Database("sample-limit");
        samples.Initialize();
        samples.CommitBatch(8, Enumerable.Range(1, 60).Select(i => Item(8, i, 2000000000)).ToArray(), null, Cutoff);
        Check(samples.GetSyncState(8).SampleCount == 50 && samples.GetSyncState(8).SampleSum == 100000000000L && samples.GetSyncState(8).ComputedThreshold == 1800000000, "F07 sample cap and 64-bit sum handle large reaction counts without overflow");

        using var aged = Database("aged-hot-rows");
        aged.Initialize();
        aged.SetCustomThreshold(9, 10);
        aged.UpsertMessages(9, new[] { Item(9, 1, 5), Item(9, 2, 20) });
        aged.GetTopMessages(9);
        aged.CommitBatch(9, Array.Empty<HotMessageItem>(), null, Cutoff);
        Check(aged.GetTotalHotCount(9) == 1 && aged.GetTopMessages(9).Single().MaxReactionCount == 20, "F09 metadata checkpoint prunes rows that aged into the cold window and invalidates their cache");

        using var zero = Database("zero");
        zero.Initialize();
        zero.UpsertMessages(6, new[] { Item(6, 1, 50, Now) });
        zero.GetTopMessages(6);
        zero.ApplyUpdates(6, Array.Empty<long>(), new[] { (Id(1), 0, (string)null, (string)null) });
        Check(zero.GetTopMessages(6).Count == 0, "F06 cached rank clears on zero reactions");
        zero.ApplyUpdates(6, Array.Empty<long>(), new[] { (Id(1), 20, "👍", "{\"👍\":20}") });
        Check(zero.GetTopMessages(6).Single().MaxReactionCount == 20, "F06 cleared reaction can become positive again without rescanning");
        zero.CommitBatch(6, new[] { Item(6, 1, 0, Now) }, null, Cutoff);
        Check(zero.GetTopMessages(6).Count == 0, "F06 history synchronization also clears stale positive reactions");
    }

    private static void ReactionTests()
    {
        var info = new MessageInteractionInfo { Reactions = new() { Reactions = new()
        {
            new() { Type = new ReactionTypeCustomEmoji { CustomEmojiId = 111 }, TotalCount = 50 },
            new() { Type = new ReactionTypeCustomEmoji { CustomEmojiId = 222 }, TotalCount = 60 },
            new() { Type = new ReactionTypePaid(), TotalCount = 20 },
            new() { Type = new ReactionTypeEmoji { Emoji = "👍" }, TotalCount = 10 }
        } } };
        var result = HotReactionsService.ExtractReactions(info);
        var counts = ReactionSentimentService.ParseReactionsJson(result.ReactionsJson);
        Check(result.TopCount == 60 && counts.Count == 4 && counts["custom:111"] == 50 && counts["paid"] == 20, "F06 custom and paid reactions retain independent identities");
        var sentiments = new ReactionSentimentService();
        var item = new HotMessageItem { MaxReactionCount = result.TopCount, TopEmoji = result.TopEmoji, ReactionsJson = result.ReactionsJson };
        Check(sentiments.EvaluateMessage(item, HotRankMode.NetPositive).Score == 10, "F06 paid and unclassified custom reactions do not inflate net positive score");
        sentiments.SetCustomCategory("custom:222", SentimentCategory.Positive);
        sentiments.SetCustomCategory("custom:111", SentimentCategory.Negative);
        Check(sentiments.EvaluateMessage(item, HotRankMode.NetPositive).Score == 10, "F06 different custom reactions can be classified separately");
        sentiments.SetCustomCategory("paid", SentimentCategory.Positive);
        Check(sentiments.GetCategory("paid") == null, "F06 paid reactions remain exclusive to the all-reactions rank");
        info.Reactions.Reactions.Add(new() { Type = new ReactionTypeEmoji { Emoji = "👍" }, TotalCount = 15 });
        counts = ReactionSentimentService.ParseReactionsJson(HotReactionsService.ExtractReactions(info).ReactionsJson);
        Check(counts["👍"] == 15, "F06 duplicate reaction identities never sum into a bigger single reaction");
        Check(HotReactionsService.FloodWaitSeconds(new Error { Code = 429, Message = "FLOOD_WAIT_900" }) == 900, "F12 long FLOOD_WAIT is not truncated");
        Check(HotReactionsService.FloodWaitSeconds(new Error { Code = 420, Message = "FLOOD_WAIT_900_OR_STARS_10" }) == 900, "F12 FLOOD_WAIT suffix never replaces the server wait duration");
        Check(HotReactionsService.FloodWaitSeconds(new Error { Code = 429, Message = "Too Many Requests: retry after 123" }) == 123, "F12 TDLib retry-after format is honored");
    }

    private static async Task ServiceTests()
    {
        var client = History(new[] { Msg(3, 100), Msg(2, 10), Msg(1, 1) });
        var service = Service(client, Database("samples"));
        try
        {
            await service.InitializeAsync();
            await service.StopColdCrawler(10);
            await service.SyncHotWindowAsync(client, 10);
            await service.SyncHotWindowAsync(client, 10);
            Check(service.Database.GetSyncState(10).SampleCount == 3, "F07 repeated hot refresh samples unique message IDs only");
            await Scan(service, client, 10);
            var state = service.Database.GetSyncState(10);
            Check(state.ComputedThreshold == 33 && state.SampleSum == 111, "F07 pending-hot completion finalizes a small-channel average");
        }
        finally { await service.ShutdownAsync(); }

        client = History(new[] { Msg(3, 10, Old + 2), Msg(2, 10, Old + 1), Msg(1, 10, Old) });
        service = Service(client, Database("small-cold"));
        try
        {
            await Scan(service, client, 11);
            Check(service.Database.GetSyncState(11).ComputedThreshold == 9, "F07 normal cold completion also finalizes a small-channel average");
        }
        finally { await service.ShutdownAsync(); }

        var entered = Source<bool>(); var response = Source<Object>();
        client = new FakeClient { Handler = q => { entered.TrySetResult(true); return response.Task; } };
        service = Service(client, Database("concurrent-threshold"));
        try
        {
            await service.InitializeAsync();
            Seed(service.Database, 12, s => { s.OldestSyncedMsgId = Id(10); s.EarliestMsgId = Id(1); s.EarliestMsgDate = Old; });
            var finished = Source<string>();
            service.StartColdCrawler(client, 12, (status, running) => { if (!running) finished.TrySetResult(status); });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            service.SetUserCustomThreshold(12, 77);
            response.SetResult(Batch(Msg(9, 100, Old + 1), Msg(1, 100, Old)));
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(service.Database.GetSyncState(12).CustomThreshold == 77, "F03 in-flight history commits do not overwrite a newer custom threshold");
        }
        finally { await service.ShutdownAsync(); }

        entered = Source<bool>(); response = Source<Object>();
        client = new FakeClient { Handler = q => { entered.TrySetResult(true); return q.FromMessageId == 0 ? response.Task : Task.FromResult<Object>(Batch()); } };
        service = Service(client, Database("first-batch-delete"));
        try
        {
            await service.InitializeAsync();
            await service.StopColdCrawler(17);
            var sync = service.SyncHotWindowAsync(client, 17);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            service.MarkMessagesDeleted(17, new[] { Id(90) });
            await Until(() => service.Database.GetRevision(17) > 0, "delete before first history reply");
            response.SetResult(Batch(Msg(90, 100), Msg(80, 10)));
            await sync;
            Check(service.Database.GetTotalHotCount(17) == 1 && service.Database.GetSyncState(17).SampleSum == 10, "F08 deletion before the first batch is indexed is retained and excluded from samples");
        }
        finally { await service.ShutdownAsync(); }

        var history = Enumerable.Range(1, 2100).Select(i => Msg(i, 10, i <= 1000 ? Old : Now)).ToArray();
        client = History(history);
        service = Service(client, Database("new-history-gap"));
        try
        {
            await service.InitializeAsync();
            service.Database.UpsertMessages(13, Enumerable.Range(1, 1000).Select(i => Item(13, i)));
            Seed(service.Database, 13, s => { s.NewestSyncedMsgId = Id(1000); s.OldestSyncedMsgId = Id(1); s.ColdSyncCompleted = true; s.EarliestMsgId = Id(1); s.EarliestMsgDate = Old; s.SampleCount = 50; s.SampleSum = 500; s.ComputedThreshold = 9; });
            await service.StopColdCrawler(13);
            await service.SyncHotWindowAsync(client, 13);
            Check(service.Database.GetTotalHotCount(13) == 1500 && service.Database.GetSyncState(13).PendingSyncFromId > 0, "F04 foreground budget records a durable pending history interval");
            await Scan(service, client, 13);
            Check(service.Database.GetTotalHotCount(13) == 2100 && service.Database.GetSyncState(13).PendingSyncFromId == 0, "F04 background catch-up fills all new messages despite completed cold history");
        }
        finally { await service.ShutdownAsync(); }

        var secondEntered = Source<bool>(); var secondResponse = Source<Object>(); int calls = 0;
        client = new FakeClient { Handler = q =>
        {
            if (q.Offset < 0) return Task.FromResult<Object>(Batch(Msg(1, 100, Old)));
            if (Interlocked.Increment(ref calls) == 1) return Task.FromResult<Object>(Batch(Enumerable.Range(52, 100).Reverse().Select(i => Msg(i, 100, Old)).ToArray()));
            secondEntered.TrySetResult(true); return secondResponse.Task;
        } };
        service = Service(client, Database("same-timestamp"));
        try
        {
            var finished = Source<bool>();
            service.StartColdCrawler(client, 14, (status, running) => { if (!running) finished.TrySetResult(true); });
            await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!service.Database.GetSyncState(14).ColdSyncCompleted, "F05 equal timestamps do not mark the scan complete before the earliest ID");
            secondResponse.SetResult(Batch(Enumerable.Range(1, 51).Reverse().Select(i => Msg(i, 100, Old)).ToArray()));
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(service.Database.GetTotalHotCount(14) == 151 && service.Database.GetSyncState(14).ColdSyncCompleted, "F05 scan includes the earliest ID before completing");
        }
        finally { await service.ShutdownAsync(); }

        secondEntered = Source<bool>(); secondResponse = Source<Object>(); calls = 0;
        client = new FakeClient { Handler = q =>
        {
            if (Interlocked.Increment(ref calls) == 1) return Task.FromResult<Object>(Batch());
            secondEntered.TrySetResult(true); return secondResponse.Task;
        } };
        service = Service(client, Database("empty-page"));
        try
        {
            await service.InitializeAsync();
            Seed(service.Database, 18, s => { s.OldestSyncedMsgId = Id(10); s.EarliestMsgId = Id(1); s.EarliestMsgDate = Old; });
            var finished = Source<bool>();
            service.StartColdCrawler(client, 18, (status, running) => { if (!running) finished.TrySetResult(true); });
            await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!service.Database.GetSyncState(18).ColdSyncCompleted && service.IsCrawlerRunning(18), "F05 transient empty page retries rather than claiming completion");
            secondResponse.SetResult(Batch(Msg(1, 10, Old)));
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(service.Database.GetSyncState(18).ColdSyncCompleted && service.Database.GetTotalHotCount(18) == 1, "F05 retry resumes from the same cursor and reaches the actual boundary");
        }
        finally { await service.ShutdownAsync(); }

        entered = Source<bool>(); response = Source<Object>(); calls = 0;
        client = new FakeClient { Handler = q =>
        {
            if (Interlocked.Increment(ref calls) == 1) { entered.TrySetResult(true); return response.Task; }
            return Task.FromResult<Object>(q.Offset < 0 ? Batch(Msg(1, 10, Old)) : Batch(Msg(9, 10, Old)));
        } };
        service = Service(client, Database("refresh-lifetime"));
        try
        {
            await service.InitializeAsync();
            Seed(service.Database, 15, s => { s.OldestSyncedMsgId = Id(10); s.EarliestMsgId = Id(1); s.EarliestMsgDate = Old; });
            service.StartColdCrawler(client, 15);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var refresh = service.SyncHotWindowAsync(client, 15);
            await Until(() => !service.IsCrawlerRunning(15), "refresh cancels old crawler");
            response.TrySetResult(Batch(Msg(1, 999, Old)));
            await refresh;
            Check(!service.IsCrawlerPaused(15), "F10 hot refresh does not change an explicitly running crawler into a user pause");
            Check(service.IsCrawlerRunning(15) || service.Database.GetSyncState(15).ColdSyncCompleted, "F10 hot refresh resumes unfinished background work");
            await service.StopColdCrawler(15);
            await Task.Delay(30);
            Check(!service.Database.GetTopMessages(15).Any(x => x.MessageId == Id(1)), "F10 canceled native reply cannot commit into the new crawl generation");
            await service.ResetChannelSyncAsync(15);
            await Task.Delay(30);
            Check(service.Database.GetTotalHotCount(15) == 0, "F10 reset does not get repopulated by an old canceled worker");
        }
        finally { await service.ShutdownAsync(); }

        client = History(new[] { Msg(1) });
        service = Service(client, Database("account-updates"));
        try
        {
            await service.InitializeAsync();
            service.Database.CommitBatch(16, new[] { Item(16, 1, 50, Now) }, null, Cutoff);
            service.UpdateMessageReaction(16, Id(1), Info(0));
            await Until(() => service.Database.GetTotalHotCount(16) == 0, "zero reaction event");
            Check(true, "F06 account event worker persists zero reactions without a view model");
            service.UpdateMessageReaction(16, Id(1), Info(20));
            await Until(() => service.Database.GetTotalHotCount(16) == 1, "positive reaction event");
            service.MarkMessagesDeleted(16, new[] { Id(1) });
            await Until(() => service.Database.GetTotalHotCount(16) == 0, "delete event");
            service.Database.UpsertMessages(16, new[] { Item(16, 1, 50, Now) });
            Check(service.Database.GetTotalHotCount(16) == 0, "F08 account event worker handles deletion without a current chat view");
        }
        finally { await service.ShutdownAsync(); }
    }

    private static async Task UpdateWriteRetryTest()
    {
        var client = History(new[] { Msg(1) });
        var service = Service(client, Database("update-write-retry"));
        var failed = Source<bool>();
        try
        {
            await service.InitializeAsync();
            service.Database.CommitBatch(27, new[] { Item(27, 1, 50, Now) }, null, Cutoff);
            Sql(PathFor("update-write-retry"), "CREATE TRIGGER fail_delete BEFORE INSERT ON channel_hot_messages WHEN NEW.is_deleted = 1 BEGIN SELECT RAISE(ABORT,'expected delete failure'); END;");
            Telegram.Logger.ExceptionObserved = ex => failed.TrySetResult(true);
            service.MarkMessagesDeleted(27, new[] { Id(1) });
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(service.Database.GetTotalHotCount(27) == 1, "F02 failed account-event write rolls back without losing the existing row");
            Sql(PathFor("update-write-retry"), "DROP TRIGGER fail_delete;");
            await Until(() => service.Database.GetTotalHotCount(27) == 0, "automatic event write retry");
            Check(true, "F08 failed delete event retries after recovery without a second Telegram event");
        }
        finally { Telegram.Logger.ExceptionObserved = null; await service.ShutdownAsync(); }
    }

    private static async Task InitializationOrderTest()
    {
        var previous = Source<bool>();
        var client = History(new[] { Msg(1) });
        var service = new HotReactionsService(client, Database("initialization-order"), TimeSpan.Zero, previous.Task);
        try
        {
            await Task.Delay(30);
            Check(!service.InitializeAsync().IsCompleted && !File.Exists(PathFor("initialization-order")), "F01 replacement service waits for previous account workers and database close before initialization");
            previous.SetResult(true);
            await service.InitializeAsync();
            Check(File.Exists(PathFor("initialization-order")), "F01 replacement service initializes after the lifecycle barrier completes");
        }
        finally { previous.TrySetResult(true); await service.ShutdownAsync(); }
    }

    private static async Task AccountChangeTest()
    {
        var entered = Source<bool>(); var response = Source<Object>();
        var client = new FakeClient { Handler = q => { entered.TrySetResult(true); return response.Task; } };
        var service = Service(client, Database("identity-change"));
        try
        {
            await service.InitializeAsync();
            await service.StopColdCrawler(24);
            var sync = service.SyncHotWindowAsync(client, 24);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            client.Options.MyId = 202;
            response.SetResult(Batch(Msg(1, 100)));
            await sync;
            service.MarkMessagesDeleted(24, new[] { Id(1) });
            await Task.Delay(30);
            Check(service.Database.GetTotalHotCount(24) == 0 && service.Database.GetSyncState(24).SampleCount == 0 && service.Database.GetRevision(24) == 0,
                "F01 changing user identity on the same client invalidates in-flight commits and account events");
        }
        finally { await service.ShutdownAsync(); }
    }

    private static async Task RateTests()
    {
        var times = new List<long>(); var clock = Stopwatch.StartNew();
        var client = new FakeClient { Handler = q =>
        {
            lock (times) times.Add(clock.ElapsedMilliseconds);
            return Task.FromResult<Object>(Batch(Msg(1, 10, Old)));
        } };
        var service = Service(client, Database("rate-budget"), 40);
        try
        {
            var a = Source<bool>(); var b = Source<bool>();
            service.StartColdCrawler(client, 20, (status, running) => { if (!running) a.TrySetResult(true); });
            service.StartColdCrawler(client, 21, (status, running) => { if (!running) b.TrySetResult(true); });
            await Task.WhenAll(a.Task, b.Task).WaitAsync(TimeSpan.FromSeconds(5));
            long[] snapshots; lock (times) snapshots = times.ToArray();
            Check(snapshots.Length == 4 && snapshots.Zip(snapshots.Skip(1), (x, y) => y - x).All(gap => gap >= 35), "F12 multiple chats share one account request interval and concurrency gate");
        }
        finally { await service.ShutdownAsync(); }

        int requests = 0; var flooded = Source<bool>();
        client = new FakeClient { Handler = q =>
        {
            int count = Interlocked.Increment(ref requests);
            if (count == 1) return Task.FromResult<Object>(Batch(Msg(1, 10, Old)));
            return Task.FromResult<Object>(new Error { Code = 429, Message = "FLOOD_WAIT_900" });
        } };
        service = Service(client, Database("flood-shared"));
        try
        {
            service.StartColdCrawler(client, 22, (status, running) => { if (status.Contains("900")) flooded.TrySetResult(true); });
            await flooded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            service.StartColdCrawler(client, 23);
            await Task.Delay(100);
            Check(requests == 2, "F12 a server cooldown also blocks requests from other chats in the account");
        }
        finally
        {
            var timer = Stopwatch.StartNew();
            await service.ShutdownAsync();
            Check(timer.ElapsedMilliseconds < 1000, "F01 account shutdown cancels long rate waits and drains database workers");
        }
    }

    private static async Task LateFloodTest()
    {
        var entered = Source<bool>(); var response = Source<Object>(); int requests = 0;
        var client = new FakeClient { Handler = q => { Interlocked.Increment(ref requests); entered.TrySetResult(true); return response.Task; } };
        var service = Service(client, Database("late-flood"));
        using var cancellation = new CancellationTokenSource();
        try
        {
            await service.InitializeAsync();
            await service.StopColdCrawler(25);
            var sync = service.SyncHotWindowAsync(client, 25, token: cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            try { await sync; } catch (OperationCanceledException) { }
            service.StartColdCrawler(client, 26);
            await Task.Delay(50);
            Check(requests == 1, "F12 canceled native request retains its account concurrency slot until it actually completes");
            response.SetResult(new Error { Code = 429, Message = "FLOOD_WAIT_900" });
            await Task.Delay(100);
            Check(requests == 1, "F12 late native FLOOD_WAIT after foreground cancellation still blocks other chats");
        }
        finally { await service.ShutdownAsync(); }
    }

    private static void Benchmark()
    {
        using var db = Database("benchmark");
        db.Initialize();
        db.UpsertMessages(30, Enumerable.Range(1, 50000).Select(i => Item(30, i, i % 1000 + 10)));
        var cold = Stopwatch.StartNew();
        db.GetTopMessages(30, mode: HotRankMode.NetPositive);
        cold.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread(); var warm = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++) db.GetTopMessages(30, mode: HotRankMode.NetPositive);
        warm.Stop(); allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        db.UpsertMessages(30, Enumerable.Range(50001, 100).Select(i => Item(30, i, i % 1000 + 10)));
        long deltaAllocated = GC.GetAllocatedBytesForCurrentThread(); var delta = Stopwatch.StartNew();
        db.GetTopMessages(30, mode: HotRankMode.NetPositive);
        delta.Stop(); deltaAllocated = GC.GetAllocatedBytesForCurrentThread() - deltaAllocated;
        Console.WriteLine($"BENCHMARK net10 Release, real SQLite, 50k candidates: initial={cold.Elapsed.TotalMilliseconds:F2}ms; cached={warm.Elapsed.TotalMilliseconds/20:F3}ms, {allocated/20/1024.0/1024:F3}MiB/reload; +100 rows rerank={delta.Elapsed.TotalMilliseconds:F2}ms, {deltaAllocated/1024.0/1024:F3}MiB; not a UWP UI benchmark");
        Check(db.GetTopMessages(30).Count == 50100, "F11 incremental cache includes new rows");
        var before = db.GetTopMessages(30, mode: HotRankMode.Positive).Single(x => x.MessageId == Id(1));
        db.ApplyUpdates(30, Array.Empty<long>(), new[] { (Id(1), 1000, "👍", "{\"👍\":1000}") });
        var after = db.GetTopMessages(30, mode: HotRankMode.Positive).Single(x => x.MessageId == Id(1));
        Check(before.MaxReactionCount == 11 && after.MaxReactionCount == 1000, "F11 cached display snapshots do not mutate already-rendered rows");
        db.SaveSentimentConfig("👍", SentimentCategory.Negative);
        Check(db.GetTopMessages(30, mode: HotRankMode.Positive).Count == 0, "F11 sentiment version invalidates cached ranking");
    }

    private static async Task<int> Main(string[] args)
    {
        try
        {
            DatabaseTests();
            ReactionTests();
            await ServiceTests();
            await UpdateWriteRetryTest();
            await InitializationOrderTest();
            await AccountChangeTest();
            await RateTests();
            await LateFloodTest();
            if (args.Contains("--benchmark")) Benchmark();
            Console.WriteLine($"SUCCESS {_assertions} assertions; original service source + real isolated SQLite; no live Telegram or real account data");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            Directory.Delete(Windows.Storage.ApplicationData.Current.LocalFolder.Path, true);
        }
    }
}
