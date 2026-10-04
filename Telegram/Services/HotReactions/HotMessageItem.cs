//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;

namespace Telegram.Services.HotReactions
{
    public class HotMessageItem
    {
        public long ChatId { get; set; }
        public long MessageId { get; set; }
        public long Date { get; set; }
        private int _maxReactionCount;
        private string _topEmoji = "👍";
        public int MaxReactionCount
        {
            get => _maxReactionCount;
            set { if (_maxReactionCount != value) { _maxReactionCount = value; _displayCached = false; } }
        }
        public string TopEmoji
        {
            get => _topEmoji;
            set { if (_topEmoji != value) { _topEmoji = value; _displayCached = false; } }
        }
        public string Snippet { get; set; } = string.Empty;
        public string SenderName { get; set; } = string.Empty;
        public bool HasMedia { get; set; }
        public bool IsCold { get; set; }
        private string _reactionsJson;
        private IReadOnlyDictionary<string, int> _reactions;
        private HotMessageItem _display;
        private bool _displayCached;
        private HotRankMode _displayMode;
        private int _displayVersion;
        public string ReactionsJson
        {
            get => _reactionsJson;
            set
            {
                if (_reactionsJson == value) return;
                _reactionsJson = value;
                _reactions = null;
                _displayCached = false;
            }
        }

        internal IReadOnlyDictionary<string, int> Reactions => _reactions ??= ReactionSentimentService.ParseReactionsJson(ReactionsJson);

        internal HotMessageItem GetDisplay(HotRankMode mode, ReactionSentimentService sentiments)
        {
            var version = mode == HotRankMode.All ? 0 : sentiments.Version;
            if (_displayCached && _displayMode == mode && _displayVersion == version) return _display;
            var (score, badge, detail) = sentiments.EvaluateMessage(this, mode);
            _display = score > 0 ? ForDisplay(score, badge, detail) : null;
            _displayMode = mode;
            _displayVersion = version;
            _displayCached = true;
            return _display;
        }

        private HotMessageItem ForDisplay(int score, string badge, string detail)
        {
            return new HotMessageItem
            {
                ChatId = ChatId, MessageId = MessageId, Date = Date,
                MaxReactionCount = MaxReactionCount, TopEmoji = TopEmoji,
                Snippet = Snippet, SenderName = SenderName, HasMedia = HasMedia, IsCold = IsCold,
                ReactionsJson = ReactionsJson, DisplayScore = score,
                DisplayBadge = badge, SubDetailText = detail
            };
        }
        public int DisplayScore { get; set; }
        public string DisplayBadge { get; set; }
        public string SubDetailText { get; set; }
        public Windows.UI.Xaml.Visibility SubDetailVisibility => string.IsNullOrEmpty(SubDetailText) ? Windows.UI.Xaml.Visibility.Collapsed : Windows.UI.Xaml.Visibility.Visible;
        public Windows.UI.Xaml.Visibility MediaVisibility => HasMedia ? Windows.UI.Xaml.Visibility.Visible : Windows.UI.Xaml.Visibility.Collapsed;

        public string FormattedCount
        {
            get
            {
                if (MaxReactionCount >= 1000000)
                {
                    return $"{MaxReactionCount / 1000000.0:0.#}M";
                }
                if (MaxReactionCount >= 1000)
                {
                    return $"{MaxReactionCount / 1000.0:0.#}k";
                }
                return MaxReactionCount.ToString();
            }
        }

        public string BadgeText => !string.IsNullOrEmpty(DisplayBadge) ? DisplayBadge : $"{ReactionSentimentService.DisplayEmoji(TopEmoji)} {FormattedCount}";
        public string ReactionKey => TopEmoji;

        public string FormattedDate
        {
            get
            {
                if (Date <= 0)
                {
                    return string.Empty;
                }

                try
                {
                    var dt = DateTimeOffset.FromUnixTimeSeconds(Date).ToLocalTime();
                    var now = DateTimeOffset.Now;

                    if (dt.Year == now.Year)
                    {
                        return dt.ToString("MM/dd HH:mm");
                    }
                    return dt.ToString("yyyy/MM/dd");
                }
                catch
                {
                    return string.Empty;
                }
            }
        }
    }

    public class ChannelSyncState
    {
        public long ChatId { get; set; }
        public long NewestSyncedMsgId { get; set; }
        public long OldestSyncedMsgId { get; set; }
        public bool ColdSyncCompleted { get; set; }
        public int SampleCount { get; set; }
        public long SampleSum { get; set; }
        public int ComputedThreshold { get; set; } = 1;
        public int? CustomThreshold { get; set; }
        public long LastHotSync { get; set; }
        public long EarliestMsgId { get; set; }
        public long EarliestMsgDate { get; set; }
        public long PendingSyncFromId { get; set; }
        public long PendingSyncNewestId { get; set; }
        public long PendingSyncStopId { get; set; }
        public int IndexedThreshold { get; set; } = 1;

        public int EffectiveThreshold => CustomThreshold ?? (ComputedThreshold > 0 ? ComputedThreshold : 1);
    }
}
