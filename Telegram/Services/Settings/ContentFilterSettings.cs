//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;

namespace Telegram.Services.Settings
{
    public sealed class ContentFilterRule
    {
        public string Pattern { get; set; } = string.Empty;
        public bool IsEnabled { get; set; } = true;
    }

    public sealed class ChannelContentFilterConfiguration
    {
        public bool IsEnabled { get; set; } = true;
        public List<ContentFilterRule> Rules { get; } = new();
    }

    public sealed class ContentFilterSettings
    {
        public const int MaxPatternLength = 2048;
        public const int MaxRuleCount = 64;

        private readonly ISettingsStore _store;
        private readonly object _sync = new();
        private readonly Dictionary<long, Entry> _entries = new();
        private int _version;

        public ContentFilterSettings(ISettingsStore store)
        {
            _store = store.GetContainer("ContentFilters");
        }

        public event EventHandler<long> Changed;

        public ChannelContentFilterConfiguration Get(long chatId)
        {
            lock (_sync)
            {
                return Copy(GetEntry(chatId).Configuration);
            }
        }

        public int GetVersion(long chatId)
        {
            lock (_sync)
            {
                return GetEntry(chatId).Version;
            }
        }

        public bool IsMatch(long chatId, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            Entry entry;
            lock (_sync)
            {
                entry = GetEntry(chatId);
            }

            if (!entry.Configuration.IsEnabled)
            {
                return false;
            }

            for (int i = 0; i < entry.Patterns.Length; i++)
            {
                var pattern = Volatile.Read(ref entry.Patterns[i]);
                if (pattern == null)
                {
                    continue;
                }

                try
                {
                    if (pattern.IsMatch(text))
                    {
                        return true;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    // Disable only this rule; later independent rules must still be checked.
                    Interlocked.Exchange(ref entry.Patterns[i], null);
                }
            }

            return false;
        }

        public static bool TryValidate(string pattern, out string error)
        {
            try
            {
                Compile(pattern);
                error = null;
                return true;
            }
            catch (ArgumentException ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public void Save(long chatId, ChannelContentFilterConfiguration configuration)
        {
            var copy = Copy(configuration);
            if (copy.Rules.Count > MaxRuleCount)
            {
                throw new ArgumentException($"最多支持 {MaxRuleCount} 条规则。", nameof(configuration));
            }

            var patterns = new Regex[copy.Rules.Count];
            for (int i = 0; i < copy.Rules.Count; i++)
            {
                if (string.IsNullOrEmpty(copy.Rules[i].Pattern) || copy.Rules[i].Pattern.Length > MaxPatternLength)
                {
                    throw new ArgumentException($"正则不能为空，且最多 {MaxPatternLength} 个字符。", nameof(configuration));
                }
                if (copy.Rules[i].IsEnabled)
                {
                    patterns[i] = Compile(copy.Rules[i].Pattern);
                }
            }

            lock (_sync)
            {
                var store = _store.GetContainer($"{chatId}");
                var previousCount = store.GetValueOrDefault("Count", 0);
                for (int i = 0; i < copy.Rules.Count; i++)
                {
                    store.SetValue($"Pattern{i}", copy.Rules[i].Pattern);
                    store.SetValue($"Enabled{i}", copy.Rules[i].IsEnabled);
                }
                for (int i = copy.Rules.Count; i < previousCount && i < MaxRuleCount; i++)
                {
                    store.Remove($"Pattern{i}");
                    store.Remove($"Enabled{i}");
                }
                store.SetValue("IsEnabled", copy.IsEnabled);
                store.SetValue("Count", copy.Rules.Count);
                _store.Flush();
                _entries[chatId] = new Entry(copy, patterns, ++_version);
            }

            Changed?.Invoke(this, chatId);
        }

        private Entry GetEntry(long chatId)
        {
            if (_entries.TryGetValue(chatId, out var entry))
            {
                return entry;
            }

            var configuration = new ChannelContentFilterConfiguration();
            if (_store.TryGetContainer($"{chatId}", out var store))
            {
                configuration.IsEnabled = store.GetValueOrDefault("IsEnabled", true);
                var count = Math.Max(0, Math.Min(MaxRuleCount, store.GetValueOrDefault("Count", 0)));
                for (int i = 0; i < count; i++)
                {
                    configuration.Rules.Add(new ContentFilterRule
                    {
                        Pattern = store.GetValueOrDefault($"Pattern{i}", string.Empty),
                        IsEnabled = store.GetValueOrDefault($"Enabled{i}", true)
                    });
                }
            }

            var patterns = new Regex[configuration.Rules.Count];
            for (int i = 0; i < patterns.Length; i++)
            {
                var rule = configuration.Rules[i];
                if (rule.IsEnabled && TryValidate(rule.Pattern, out _))
                {
                    patterns[i] = Compile(rule.Pattern);
                }
            }

            entry = new Entry(configuration, patterns, ++_version);
            _entries[chatId] = entry;
            return entry;
        }

        private static Regex Compile(string pattern)
        {
            if (string.IsNullOrEmpty(pattern) || pattern.Length > MaxPatternLength)
            {
                throw new ArgumentException($"正则不能为空，且最多 {MaxPatternLength} 个字符。");
            }

            return new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(20));
        }

        private static ChannelContentFilterConfiguration Copy(ChannelContentFilterConfiguration configuration)
        {
            var copy = new ChannelContentFilterConfiguration { IsEnabled = configuration.IsEnabled };
            foreach (var rule in configuration.Rules)
            {
                copy.Rules.Add(new ContentFilterRule { Pattern = rule.Pattern, IsEnabled = rule.IsEnabled });
            }
            return copy;
        }

        private sealed class Entry
        {
            public Entry(ChannelContentFilterConfiguration configuration, Regex[] patterns, int version)
            {
                Configuration = configuration;
                Patterns = patterns;
                Version = version;
            }

            public ChannelContentFilterConfiguration Configuration { get; }
            public Regex[] Patterns { get; }
            public int Version { get; }
        }
    }
}
