using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Services;
using Telegram.Services.Settings;
using Telegram.Td.Api;

static class Program
{
    private static int _checks;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        _checks++;
        Console.WriteLine("PASS " + name);
    }
    private static ChannelContentFilterConfiguration Config(params string[] patterns)
    {
        var result = new ChannelContentFilterConfiguration();
        foreach (var pattern in patterns) result.Rules.Add(new ContentFilterRule { Pattern = pattern });
        return result;
    }
    private static void Throws(Action action, string name)
    {
        try { action(); } catch (ArgumentException) { Check(true, name); return; }
        Check(false, name);
    }

    public static async Task<int> Main()
    {
        var root = new MemoryStore();
        var accountA = root.GetContainer("1001");
        var accountB = root.GetContainer("1002");
        var filters = new ContentFilterSettings(accountA);
        Check(!filters.IsMatch(777, "广告"), "empty channel has no filtering");
        Check(!filters.IsMatch(777, null), "null caption is not filtered");
        long changed = 0;
        filters.Changed += (s, chat) => changed = chat;
        var configuration = Config("推广|广告合作", @"(?i)example\.com");
        filters.Save(777, configuration);
        Check(changed == 777, "save publishes exact channel");
        Check(filters.IsMatch(777, "广告合作请联系"), "first rule matches");
        Check(filters.IsMatch(777, "EXAMPLE.com"), "inline ignore-case and any-rule matching");
        Check(!filters.IsMatch(777, "普通教程"), "unmatched content is retained");
        Check(!filters.IsMatch(778, "广告合作"), "channel isolation");
        Check(!new ContentFilterSettings(accountB).IsMatch(777, "广告合作"), "account isolation");
        Check(new ContentFilterSettings(accountA).IsMatch(777, "广告合作"), "fresh service restores persisted rules");
        configuration.Rules[0].Pattern = "外部修改";
        Check(filters.IsMatch(777, "广告合作"), "save detaches caller configuration");
        var editorCopy = filters.Get(777);
        editorCopy.Rules.Clear();
        Check(filters.Get(777).Rules.Count == 2, "get returns detached editable copy");
        var oldVersion = filters.GetVersion(777);
        var disabled = filters.Get(777);
        disabled.Rules[0].IsEnabled = false;
        filters.Save(777, disabled);
        Check(!filters.IsMatch(777, "广告合作"), "individual rule disable");
        Check(filters.IsMatch(777, "example.com"), "other enabled rule unaffected");
        Check(oldVersion != filters.GetVersion(777), "save invalidates explicit reveal version");
        disabled.IsEnabled = false;
        filters.Save(777, disabled);
        Check(!filters.IsMatch(777, "example.com"), "channel master switch");
        Check(!ContentFilterSettings.TryValidate("[", out var error) && !string.IsNullOrEmpty(error), "invalid regex has an error");
        Throws(() => filters.Save(777, Config("[")), "invalid active regex rejected before persistence");
        Check(!filters.IsMatch(777, "example.com"), "failed save preserves previous settings");
        var dormant = Config("[");
        dormant.Rules[0].IsEnabled = false;
        filters.Save(777, dormant);
        Check(!new ContentFilterSettings(accountA).IsMatch(777, "anything"), "invalid disabled rule remains harmless");
        Throws(() => filters.Save(777, Config(new string('x', ContentFilterSettings.MaxPatternLength + 1))), "pattern length limit");
        Throws(() => filters.Save(777, Config(Enumerable.Repeat("x", ContentFilterSettings.MaxRuleCount + 1).ToArray())), "rule count limit");
        filters.Save(777, Config("a", "b", "c"));
        filters.Save(777, Config("z"));
        Check(new ContentFilterSettings(accountA).Get(777).Rules.Count == 1, "removed rules do not return after reload");
        Check(!new ContentFilterSettings(accountA).IsMatch(777, "b"), "stale rule storage cleaned");
        filters.Save(778, Config(@"(?s)start.*end"));
        Check(filters.IsMatch(778, "start\nend"), "multiline text with inline singleline option");
        filters.Save(779, Config("AD"));
        Check(!filters.IsMatch(779, "ad"), "default case sensitivity");
        filters.Save(780, Config("(a+)+$"));
        var text = new string('a', 20000) + "!";
        var timer = Stopwatch.StartNew();
        Check(!filters.IsMatch(780, text), "catastrophic regex fails open");
        var firstMs = timer.ElapsedMilliseconds;
        timer.Restart();
        Check(!filters.IsMatch(780, text), "timed-out rule stays inactive");
        var nextMs = timer.ElapsedMilliseconds;
        Check(firstMs < 2000 && nextMs < 1000, "bounded regex execution");
        Console.WriteLine($"TIMEOUT first_ms={firstMs} next_ms={nextMs}");
        filters.Save(781, Config("(a+)+$", "!$"));
        Check(filters.IsMatch(781, text), "timeout does not skip a later matching rule");
        Check(filters.IsMatch(781, text), "later rule still matches after offender is disabled");
        filters.Save(782, Config("does-not-match", "(a+)+$", "!$"));
        Check(filters.IsMatch(782, text), "middle rule timeout preserves any-rule semantics");
        filters.Save(777, Config());
        Check(!filters.IsMatch(777, "z"), "clearing all rules restores content");
        accountA.DeleteContainer("ContentFilters");
        Check(!new ContentFilterSettings(accountA).IsMatch(779, "AD"), "account cleanup removes filter settings");

        var bubble = new Telegram.Controls.Messages.MessageBubbleLifecycleProbe();
        bubble.RegisterEvents();
        Check(!bubble.IgnoreSizeChanged, "prepared bubble enables size animation");
        bubble.FoldForProbe();
        Check(bubble.IsContentFiltered && !bubble.IgnoreSizeChanged && bubble.ContentRecycles == 1, "in-place fold cleans content without unregistering animation");
        bubble.RevealForProbe();
        Check(!bubble.IgnoreSizeChanged, "revealed bubble retains animation registration");
        bubble.Recycle();
        Check(bubble.IgnoreSizeChanged, "real container recycle still unregisters animation");
        var deferred = new Telegram.Controls.Messages.MessageBubbleLifecycleProbe();
        deferred.FoldForProbe();
        Check(deferred.IgnoreSizeChanged, "fold preserves deferred container preparation");
        deferred.RegisterEvents();
        deferred.RevealForProbe();
        Check(!deferred.IgnoreSizeChanged, "container preparation during fold remains effective on reveal");

        var client = new ClientServiceSendProbe();
        Telegram.Td.Api.Object result = null;
        client.Send(new GetChatSponsoredMessages(), response => result = response);
        Check(result is SponsoredMessages { Messages.Length: 0, MessagesBetween: 0 }, "channel and bot ads return empty response");
        client.Send(new GetVideoMessageAdvertisements(), response => result = response);
        Check(result is VideoMessageAdvertisements { Advertisements.Length: 0 }, "video ads return empty response");
        client.Send(new GetSearchSponsoredChats(), response => result = response);
        Check(result is SponsoredChats { Chats.Length: 0 }, "search ads return empty response");
        client.Send(new GetChatSponsoredMessages());
        Check(client.NativeRequests == 0, "blocked requests never reach native client, even without a callback");
        Check(await client.SendAsync(new GetChatSponsoredMessages()) is SponsoredMessages { Messages.Length: 0 }, "async chat ads intercepted");
        Check(await client.SendAsync(new GetVideoMessageAdvertisements()) is VideoMessageAdvertisements { Advertisements.Length: 0 }, "async video ads intercepted");
        Check(await client.SendAsync(new GetSearchSponsoredChats()) is SponsoredChats { Chats.Length: 0 }, "async search ads intercepted");
        Check(client.NativeRequests == 0, "async blocked requests never reach native client");
        client.Send(new OrdinaryFunction(), response => result = response);
        Check(result is Ok && client.NativeRequests == 1, "ordinary callback request passes through");
        Check(await client.SendAsync(new OrdinaryFunction()) is Ok && client.NativeRequests == 2, "ordinary async request passes through");
        Console.WriteLine($"RESULT checks={_checks} all_passed=true");
        return 0;
    }
}
