using System;
using System.Collections.Generic;

namespace Telegram.Services.Settings
{
    // Exact store contract; an in-memory implementation never touches application data.
    public interface ISettingsStore
    {
        bool TryGetValue(string key, out object value);
        void SetValue(string key, object value);
        bool ContainsKey(string key);
        void Remove(string key);
        void Clear();
        IEnumerable<string> ContainerNames { get; }
        ISettingsStore GetContainer(string name);
        bool TryGetContainer(string name, out ISettingsStore container);
        void DeleteContainer(string name);
        void Flush();
    }
    public static class SettingsStoreExtensions
    {
        public static T GetValueOrDefault<T>(this ISettingsStore store, string key, T fallback)
            => store.TryGetValue(key, out var value) && value is T result ? result : fallback;
    }
    public sealed class MemoryStore : ISettingsStore
    {
        private readonly Dictionary<string, object> _values = new();
        private readonly Dictionary<string, ISettingsStore> _containers = new();
        public IEnumerable<string> ContainerNames => _containers.Keys;
        public bool TryGetValue(string key, out object value) => _values.TryGetValue(key, out value);
        public void SetValue(string key, object value) => _values[key] = value;
        public bool ContainsKey(string key) => _values.ContainsKey(key);
        public void Remove(string key) => _values.Remove(key);
        public void Clear() => _values.Clear();
        public ISettingsStore GetContainer(string name)
        {
            if (!_containers.TryGetValue(name, out var store)) _containers[name] = store = new MemoryStore();
            return store;
        }
        public bool TryGetContainer(string name, out ISettingsStore container) => _containers.TryGetValue(name, out container);
        public void DeleteContainer(string name) => _containers.Remove(name);
        public void Flush() { }
    }
}

namespace Telegram.Td.Api
{
    // Only the response shapes used by the real Send methods. The full app build checks TDLib bindings.
    public abstract class Object { }
    public abstract class Function : Object { }
    public sealed class GetChatSponsoredMessages : Function { }
    public sealed class GetVideoMessageAdvertisements : Function { }
    public sealed class GetSearchSponsoredChats : Function { }
    public sealed class OrdinaryFunction : Function { }
    public sealed class Ok : Object { }
    public sealed class SponsoredMessage { }
    public sealed class SponsoredChat { }
    public sealed class VideoMessageAdvertisement { }
    public sealed class SponsoredMessages : Object
    {
        public SponsoredMessages(SponsoredMessage[] messages, int messagesBetween) { Messages = messages; MessagesBetween = messagesBetween; }
        public SponsoredMessage[] Messages { get; }
        public int MessagesBetween { get; }
    }
    public sealed class SponsoredChats : Object
    {
        public SponsoredChats(SponsoredChat[] chats) { Chats = chats; }
        public SponsoredChat[] Chats { get; }
    }
    public sealed class VideoMessageAdvertisements : Object
    {
        public VideoMessageAdvertisements(VideoMessageAdvertisement[] advertisements, int startDelay, int betweenDelay)
        { Advertisements = advertisements; StartDelay = startDelay; BetweenDelay = betweenDelay; }
        public VideoMessageAdvertisement[] Advertisements { get; }
        public int StartDelay { get; }
        public int BetweenDelay { get; }
    }
}

namespace Telegram.Services
{
    public sealed class FakeNativeClient
    {
        public int Requests { get; private set; }
        public void Send(Telegram.Td.Api.Function function, Action<Telegram.Td.Api.Object> handler)
        {
            Requests++;
            handler?.Invoke(new Telegram.Td.Api.Ok());
        }
    }
}
