global using Object = Telegram.Td.Api.Object;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Windows.UI.Xaml { public enum Visibility { Visible, Collapsed } }
namespace Telegram.Common { }
namespace Telegram { public static class Logger { public static Action<Exception> ExceptionObserved; public static void Error(string error) => Console.WriteLine("SOURCE ERROR: " + error); public static void Exception(Exception ex) { Console.WriteLine("SOURCE EXCEPTION: " + ex.Message); ExceptionObserved?.Invoke(ex); } } }
namespace Telegram.Services
{
    public interface IClientService
    {
        int SessionId { get; }
        FakeOptions Options { get; }
        Task<Telegram.Td.Api.Object> SendAsync(Telegram.Td.Api.GetChatHistory query);
        bool TryGetUser(long id, out User user);
        bool TryGetChat(long id, out Chat chat);
    }
    public class FakeOptions { public long MyId = 101; }
    public class User { public string FirstName = "Test"; public string LastName = "User"; }
    public class Chat { public string Title = "Test channel"; }
    public class FakeClient : IClientService
    {
        public int SessionId { get; set; } = 1;
        public FakeOptions Options { get; } = new();
        public Func<Telegram.Td.Api.GetChatHistory, Task<Telegram.Td.Api.Object>> Handler;
        public List<Telegram.Td.Api.GetChatHistory> Requests = new();
        public Task<Telegram.Td.Api.Object> SendAsync(Telegram.Td.Api.GetChatHistory q) { lock (Requests) Requests.Add(q); return Handler(q); }
        public bool TryGetUser(long id, out User user) { user = new(); return true; }
        public bool TryGetChat(long id, out Chat chat) { chat = new(); return true; }
    }
}
namespace Telegram.Td.Api
{
    public class Object { }
    public class Error : Object { public int Code; public string Message; }
    public class GetChatHistory { public long ChatId, FromMessageId; public int Offset, Limit; public bool OnlyLocal; public GetChatHistory(long c, long f, int o, int l, bool local) { ChatId=c; FromMessageId=f; Offset=o; Limit=l; OnlyLocal=local; } }
    public class Messages : Object { public List<Message> MessagesValue = new(); }
    public class Message { public long Id, Date; public MessageInteractionInfo InteractionInfo; public MessageContent Content = new MessageText(); public MessageSender SenderId = new MessageSenderUser(); }
    public class MessageInteractionInfo { public MessageReactions Reactions = new(); }
    public class MessageReactions { public List<MessageReaction> Reactions = new(); }
    public class MessageReaction { public ReactionType Type; public int TotalCount; }
    public class ReactionType { }
    public class ReactionTypeEmoji : ReactionType { public string Emoji; }
    public class ReactionTypeCustomEmoji : ReactionType { public long CustomEmojiId; }
    public class ReactionTypePaid : ReactionType { }
    public class MessageSender { }
    public class MessageSenderUser : MessageSender { public long UserId; }
    public class MessageSenderChat : MessageSender { public long ChatId; }
    public class MessageContent { }
    public class FormattedText { public string Text = "test"; }
    public class MessageText : MessageContent { public FormattedText Text = new(); }
    public class MessagePhoto : MessageContent { public FormattedText Caption = new(); }
    public class MessageVideo : MessageContent { public FormattedText Caption = new(); }
    public class Document { public string FileName; }
    public class MessageDocument : MessageContent { public FormattedText Caption = new(); public Document Document = new(); }
    public class MessageAnimation : MessageContent { public FormattedText Caption = new(); }
    public class Poll { public string Question; }
    public class MessagePoll : MessageContent { public Poll Poll = new(); }
}

namespace Windows.Storage { public class StorageFolder { public string Path { get; set; } } public class ApplicationData { public static ApplicationData Current { get; } = new(); public StorageFolder LocalFolder { get; } = new() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hot-reactions-tests-" + System.Guid.NewGuid().ToString("N")) }; static ApplicationData() { System.IO.Directory.CreateDirectory(Current.LocalFolder.Path); } } }
