using System.Collections.Generic;
using System.Threading.Tasks;
using Telegram.Navigation.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Windows.UI.Xaml
{
    public sealed class XamlRoot { }
}

namespace Windows.UI.Xaml.Controls
{
    public enum ContentDialogResult { None, Primary, Secondary }
}

namespace Windows.UI.Xaml.Navigation
{
    public enum NavigationMode { New }
}

namespace Telegram.Td.Api
{
    public sealed class Chat { public long Id { get; set; } }
    public sealed class MessageTopic { }
}

namespace Telegram.Controls
{
    public partial class OverlayWindow
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<XamlRoot, OverlayWindow> _instances = new();
        public int OpenedCalls { get; private set; }
        public int ClosedCalls { get; private set; }
        public int HideCalls { get; private set; }
        public ContentDialogResult LastResult { get; private set; }
        public void PopupOpened() => OpenedCalls++;
        public void PopupClosed() => ClosedCalls++;
        public void TryHide(ContentDialogResult result) { HideCalls++; LastResult = result; }
        public static void Seed(XamlRoot root, OverlayWindow window) => _instances.Add(root, window);
        public static bool HasEntry(XamlRoot root) => _instances.TryGetValue(root, out _);
    }
}

namespace Telegram.Navigation.Services
{
    public sealed class NavigationState : Dictionary<string, object> { }
    public sealed class ChatMessageTopic
    {
        public long ChatId { get; }
        public Telegram.Td.Api.MessageTopic MessageTopic { get; }
        public ChatMessageTopic(long chatId, Telegram.Td.Api.MessageTopic topic) { ChatId = chatId; MessageTopic = topic; }
    }
    public sealed class Frame
    {
        public object Content { get; set; }
        public List<object> ForwardStack { get; } = new();
    }
    public sealed class FrameFacade
    {
        public object Parameter { get; private set; }
        public void RaiseNavigated(object parameter) => Parameter = parameter;
    }
    public partial class NavigationProbe
    {
        public Frame Frame { get; } = new();
        public FrameFacade FrameFacade { get; } = new();
        public object Dispatcher { get; } = new();
        public XamlRoot XamlRoot { get; } = new();
        public int BackStackClears { get; private set; }
        private void GoBackAt(int index, bool back) => BackStackClears++;
    }
}

namespace Telegram.ViewModels
{
    public sealed class DialogViewModel
    {
        public object NavigationService { get; set; }
        public object Dispatcher { get; set; }
        public object Parameter { get; private set; }
        public NavigationState State { get; private set; }
        public bool NavigatedFromCalled { get; private set; }
        public void NavigatedFrom(NavigationState state, bool suspending) => NavigatedFromCalled = true;
        public Task NavigatedToAsync(object parameter, NavigationMode mode, NavigationState state)
        {
            Parameter = parameter;
            State = state;
            return Task.CompletedTask;
        }
    }
}

namespace Telegram.Views
{
    public sealed class ChatPage
    {
        public object DataContext { get; set; } = new object();
        public Telegram.ViewModels.DialogViewModel ViewModel => DataContext as Telegram.ViewModels.DialogViewModel;
        public int Deactivations { get; private set; }
        public int Activations { get; private set; }
        public void Deactivate(bool navigation) { Deactivations++; DataContext = new object(); }
        public void Activate(object service) { Activations++; DataContext = new Telegram.ViewModels.DialogViewModel(); }
    }
}
