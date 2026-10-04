#if NET9_0_OR_GREATER
#error This suite must compile the UWP compatibility shim, not use the native GetOrAdd implementation.
#endif

using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Telegram.Controls;
using Telegram.Navigation.Services;
using Telegram.Td.Api;
using Telegram.ViewModels;
using Telegram.Views;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Shim = System.Runtime.CompilerServices.ConditionalWeakTableExtensions;

static class Program
{
    private static int _checks;
    private static int _failures;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        _checks++;
        Console.WriteLine("PASS " + name);
    }
    private static async Task Run(string name, Func<Task> test)
    {
        try { await test(); }
        catch (Exception exception) { _failures++; Console.WriteLine("FAIL " + name + ": " + exception); }
    }

    public static async Task<int> Main()
    {
        await Run("UWP value overload", () =>
        {
            var table = new ConditionalWeakTable<object, object>();
            var key = new object();
            var value = new object();
            Check(ReferenceEquals(Shim.GetOrAdd(table, key, value), value), "first add returns supplied instance");
            Check(table.TryGetValue(key, out var stored) && ReferenceEquals(stored, value), "first add stores supplied instance");
            Check(ReferenceEquals(Shim.GetOrAdd(table, key, new object()), value), "second add preserves existing instance");
            var otherKey = new object();
            var otherValue = new object();
            Check(ReferenceEquals(Shim.GetOrAdd(table, otherKey, otherValue), otherValue), "different roots retain independent instances");
            return Task.CompletedTask;
        });
        await Run("UWP factory overload", () =>
        {
            var table = new ConditionalWeakTable<object, object>();
            var key = new object();
            var value = new object();
            var calls = 0;
            Func<object, object> factory = _ => { calls++; return value; };
            Check(ReferenceEquals(Shim.GetOrAdd(table, key, factory), value), "factory add returns supplied instance");
            Check(ReferenceEquals(Shim.GetOrAdd(table, key, factory), value) && calls == 1, "factory executes only on a cache miss");
            return Task.CompletedTask;
        });

        foreach (var operation in new[] { "opened", "closed", "hide" })
        {
            await Run("invalid overlay cache: " + operation, () =>
            {
                var root = new XamlRoot();
                OverlayWindow.Seed(root, null);
                if (operation == "opened") Check(!OverlayWindow.PopupOpened(root), "invalid opened returns false");
                else if (operation == "closed") Check(!OverlayWindow.PopupClosed(root), "invalid closed returns false");
                else OverlayWindow.TryHide(root, ContentDialogResult.None);
                Check(!OverlayWindow.HasEntry(root), operation + " removes null cache entry");
                var window = new OverlayWindow();
                OverlayWindow.Seed(root, window);
                Check(OverlayWindow.PopupOpened(root) && window.OpenedCalls == 1, operation + " allows a fresh overlay after cleanup");
                return Task.CompletedTask;
            });
        }
        await Run("valid and absent overlay caches", () =>
        {
            Check(!OverlayWindow.PopupOpened(null) && !OverlayWindow.PopupClosed(null), "null root has no overlay");
            OverlayWindow.TryHide(null, ContentDialogResult.None);
            var missing = new XamlRoot();
            Check(!OverlayWindow.PopupOpened(missing) && !OverlayWindow.PopupClosed(missing), "absent root has no overlay");
            OverlayWindow.TryHide(missing, ContentDialogResult.None);
            Check(!OverlayWindow.HasEntry(missing), "absent lookup does not create an entry");
            var root = new XamlRoot();
            var window = new OverlayWindow();
            OverlayWindow.Seed(root, window);
            Check(OverlayWindow.PopupOpened(root) && window.OpenedCalls == 1, "valid opened reaches original instance");
            Check(OverlayWindow.PopupClosed(root) && window.ClosedCalls == 1, "valid closed reaches original instance");
            OverlayWindow.TryHide(root, ContentDialogResult.Primary);
            Check(window.HideCalls == 1 && window.LastResult == ContentDialogResult.Primary, "valid hide preserves dialog result");
            Check(OverlayWindow.HasEntry(root), "valid lookup preserves cache ownership");
            return Task.CompletedTask;
        });

        foreach (var hasPrevious in new[] { false, true })
        {
            await Run("page reuse, previous model: " + hasPrevious, async () =>
            {
                var navigation = new NavigationProbe();
                var previous = hasPrevious ? new DialogViewModel() : null;
                var page = new ChatPage { DataContext = (object)previous ?? new object() };
                navigation.Frame.Content = page;
                navigation.Frame.ForwardStack.Add(new object());
                var state = new NavigationState { ["message_id"] = 42L };
                await navigation.NavigateAsync(new Chat { Id = 123 }, state: state, clearBackStack: true);
                Check(page.Activations == 1 && page.Deactivations == 1, "page is deactivated and reactivated exactly once");
                Check(page.ViewModel != null && !ReferenceEquals(page.ViewModel, previous), "page receives a fresh view model");
                Check(previous == null || previous.NavigatedFromCalled, "existing view model receives navigating-from callback");
                Check(page.ViewModel.Parameter is long chatId && chatId == 123, "target channel is initialized");
                Check(ReferenceEquals(page.ViewModel.State, state), "message navigation state is preserved");
                Check(ReferenceEquals(page.ViewModel.NavigationService, navigation) && ReferenceEquals(page.ViewModel.Dispatcher, navigation.Dispatcher), "navigation service and dispatcher are rebound");
                Check(navigation.FrameFacade.Parameter is long raised && raised == 123, "frame parameter tracks recovered channel");
                Check(navigation.Frame.ForwardStack.Count == 0 && navigation.BackStackClears == 1, "forward and requested back stacks are cleared");
            });
        }
        await Run("comment topic recovery with invalid overlay", async () =>
        {
            var navigation = new NavigationProbe();
            var page = new ChatPage();
            navigation.Frame.Content = page;
            OverlayWindow.Seed(navigation.XamlRoot, null);
            var topic = new MessageTopic();
            await navigation.NavigateAsync(new Chat { Id = 456 }, topic: topic);
            Check(page.ViewModel.Parameter is ChatMessageTopic parameter && parameter.ChatId == 456 && ReferenceEquals(parameter.MessageTopic, topic), "recovery preserves comment topic");
            Check(ReferenceEquals(navigation.FrameFacade.Parameter, page.ViewModel.Parameter), "frame and view model agree on comment topic");
            Check(navigation.BackStackClears == 0, "back stack is retained unless explicitly cleared");
            Check(!OverlayWindow.HasEntry(navigation.XamlRoot), "recovery cleans invalid overlay without interrupting initialization");
        });
        Console.WriteLine($"RESULT checks={_checks} failures={_failures}");
        return _failures == 0 ? 0 : 1;
    }
}
