//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;

namespace Telegram.Services.HotReactions
{
    public class HotMessageItem
    {
        public long ChatId { get; set; }
        public long MessageId { get; set; }
        public long Date { get; set; }
        public int MaxReactionCount { get; set; }
        public string TopEmoji { get; set; } = "👍";
        public string Snippet { get; set; } = string.Empty;
        public string SenderName { get; set; } = string.Empty;
        public bool HasMedia { get; set; }
        public bool IsCold { get; set; }
        public string ReactionsJson { get; set; }
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

        public string BadgeText => !string.IsNullOrEmpty(DisplayBadge) ? DisplayBadge : $"{TopEmoji} {FormattedCount}";

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
        public int SampleSum { get; set; }
        public int ComputedThreshold { get; set; } = 1;
        public int? CustomThreshold { get; set; }
        public long LastHotSync { get; set; }
        public long EarliestMsgId { get; set; }
        public long EarliestMsgDate { get; set; }

        public int EffectiveThreshold => CustomThreshold ?? (ComputedThreshold > 0 ? ComputedThreshold : 1);
    }
}
