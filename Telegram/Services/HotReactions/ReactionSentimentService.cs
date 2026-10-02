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
using System.Text.Json;

namespace Telegram.Services.HotReactions
{
    public enum SentimentCategory
    {
        Positive = 0,
        Negative = 1,
        Shock = 2
    }

    public enum HotRankMode
    {
        All = 0,
        NetPositive = 1,
        Positive = 2,
        Negative = 3,
        Shock = 4
    }

    public class ReactionSentimentService
    {
        private static ReactionSentimentService _current;
        public static ReactionSentimentService Current => _current ??= new ReactionSentimentService();

        private static readonly HashSet<string> DefaultPositive = new(StringComparer.Ordinal)
        {
            "👍", "❤️", "🔥", "👏", "🎉", "🥰", "🤩", "😍", "⚡", "🏆", "💯", "🤝", "👌", "😘", "🫡"
        };

        private static readonly HashSet<string> DefaultNegative = new(StringComparer.Ordinal)
        {
            "👎", "💩", "🤮", "🤬", "🤡", "💔", "🥱", "🖕", "😡", "🤐"
        };

        private static readonly HashSet<string> DefaultShock = new(StringComparer.Ordinal)
        {
            "😱", "😨", "😢", "😭", "🤯", "🤔", "👀", "🕊️", "🗿", "🌚"
        };

        private readonly ConcurrentDictionary<string, SentimentCategory> _customCategories = new(StringComparer.Ordinal);
        private readonly object _lock = new();

        public ReactionSentimentService()
        {
        }

        public void LoadCustomConfig(IDictionary<string, SentimentCategory> configs)
        {
            lock (_lock)
            {
                _customCategories.Clear();
                if (configs != null)
                {
                    foreach (var kvp in configs)
                    {
                        _customCategories[kvp.Key] = kvp.Value;
                    }
                }
            }
        }

        public SentimentCategory? GetCategory(string emoji)
        {
            if (string.IsNullOrEmpty(emoji)) return null;

            if (_customCategories.TryGetValue(emoji, out var cat))
            {
                return cat;
            }

            if (DefaultPositive.Contains(emoji)) return SentimentCategory.Positive;
            if (DefaultNegative.Contains(emoji)) return SentimentCategory.Negative;
            if (DefaultShock.Contains(emoji)) return SentimentCategory.Shock;

            return null;
        }

        public void SetCustomCategory(string emoji, SentimentCategory? category)
        {
            if (string.IsNullOrEmpty(emoji)) return;

            if (category.HasValue)
            {
                _customCategories[emoji] = category.Value;
            }
            else
            {
                _customCategories.TryRemove(emoji, out _);
            }
        }

        public void ResetToDefaults()
        {
            lock (_lock)
            {
                _customCategories.Clear();
            }
        }

        public List<string> GetEffectiveEmojis(SentimentCategory category)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);

            IEnumerable<string> defaults = category switch
            {
                SentimentCategory.Positive => DefaultPositive,
                SentimentCategory.Negative => DefaultNegative,
                SentimentCategory.Shock => DefaultShock,
                _ => Enumerable.Empty<string>()
            };

            foreach (var emoji in defaults)
            {
                // 如果用户没有把该默认表情改到别的类别，就保留
                if (!_customCategories.TryGetValue(emoji, out var userCat) || userCat == category)
                {
                    result.Add(emoji);
                }
            }

            // 加入用户自定义指定为该类别的表情
            foreach (var kvp in _customCategories)
            {
                if (kvp.Value == category)
                {
                    result.Add(kvp.Key);
                }
            }

            return result.OrderBy(x => x).ToList();
        }

        public Dictionary<string, SentimentCategory> GetAllCustomConfigs()
        {
            return new Dictionary<string, SentimentCategory>(_customCategories);
        }

        public static Dictionary<string, int> ParseReactionsJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new Dictionary<string, int>();
            }

            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? new Dictionary<string, int>();
            }
            catch
            {
                return new Dictionary<string, int>();
            }
        }

        public static string FormatCount(int count)
        {
            if (count >= 1000000)
            {
                return $"{count / 1000000.0:0.#}M";
            }
            if (count >= 1000)
            {
                return $"{count / 1000.0:0.#}k";
            }
            return count.ToString();
        }

        public (int Score, string DisplayBadge, string SubDetail) EvaluateMessage(
            HotMessageItem item,
            HotRankMode mode)
        {
            var dict = ParseReactionsJson(item.ReactionsJson);

            // 历史老数据兜底：若 reactions_json 为空，使用 TopEmoji 与 MaxReactionCount 填充
            if (dict.Count == 0 && !string.IsNullOrEmpty(item.TopEmoji) && item.MaxReactionCount > 0)
            {
                dict[item.TopEmoji] = item.MaxReactionCount;
            }

            switch (mode)
            {
                case HotRankMode.All:
                {
                    int score = item.MaxReactionCount;
                    string emoji = item.TopEmoji ?? "👍";
                    return (score, $"{emoji} {FormatCount(score)}", null);
                }

                case HotRankMode.Positive:
                {
                    var (maxCount, bestEmoji) = GetCategoryTop(dict, SentimentCategory.Positive);
                    if (maxCount <= 0) return (0, null, null);
                    return (maxCount, $"{bestEmoji} {FormatCount(maxCount)}", null);
                }

                case HotRankMode.Negative:
                {
                    var (maxCount, bestEmoji) = GetCategoryTop(dict, SentimentCategory.Negative);
                    if (maxCount <= 0) return (0, null, null);
                    return (maxCount, $"{bestEmoji} {FormatCount(maxCount)}", null);
                }

                case HotRankMode.Shock:
                {
                    var (maxCount, bestEmoji) = GetCategoryTop(dict, SentimentCategory.Shock);
                    if (maxCount <= 0) return (0, null, null);
                    return (maxCount, $"{bestEmoji} {FormatCount(maxCount)}", null);
                }

                case HotRankMode.NetPositive:
                {
                    var (maxPos, posEmoji) = GetCategoryTop(dict, SentimentCategory.Positive);
                    var (maxNeg, negEmoji) = GetCategoryTop(dict, SentimentCategory.Negative);

                    int netScore = maxPos - maxNeg;

                    // 必须有正面反应且净得分大于 0 才能入榜
                    if (maxPos <= 0 || netScore <= 0)
                    {
                        return (0, null, null);
                    }

                    string badge = $"✨ +{FormatCount(netScore)}";
                    string sub = $"{posEmoji ?? "👍"} {FormatCount(maxPos)} · {negEmoji ?? "👎"} {FormatCount(maxNeg)}";
                    return (netScore, badge, sub);
                }

                default:
                    return (item.MaxReactionCount, item.BadgeText, null);
            }
        }

        private (int MaxCount, string BestEmoji) GetCategoryTop(Dictionary<string, int> reactions, SentimentCategory targetCategory)
        {
            int max = 0;
            string bestEmoji = null;

            foreach (var kvp in reactions)
            {
                if (kvp.Value <= 0) continue;

                var cat = GetCategory(kvp.Key);
                if (cat == targetCategory)
                {
                    if (kvp.Value > max)
                    {
                        max = kvp.Value;
                        bestEmoji = kvp.Key;
                    }
                }
            }

            return (max, bestEmoji);
        }
    }
}
