//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Telegram.Common;
using Telegram.Controls;
using Telegram.Navigation;
using Telegram.Navigation.Services;
using Telegram.Services;
using Telegram.Services.HotReactions;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Telegram.Views.Popups
{
    public sealed partial class HotReactionsPopup : ContentPopup
    {
        private readonly IClientService _clientService;
        private readonly INavigationService _navigationService;
        private readonly long _chatId;

        private bool _isWeekOnly = true;
        private bool _userPausedCrawler = false;
        private HotRankMode _rankMode = HotRankMode.NetPositive;

        private int _currentPage = 1;
        private const int PageSize = 40;
        private List<HotMessageItem> _allCurrentItems = new List<HotMessageItem>();

        public HotReactionsPopup(IClientService clientService, INavigationService navigationService, long chatId)
        {
            InitializeComponent();

            _clientService = clientService;
            _navigationService = navigationService;
            _chatId = chatId;
        }

        public Visibility hasMediaVisibility(bool hasMedia) => hasMedia ? Visibility.Visible : Visibility.Collapsed;

        private async void OnPopupLoaded(object sender, RoutedEventArgs e)
        {
            UpdateFilterButtons();
            UpdateRankModeButtons();
            await RefreshDataAsync();

            // 仅在用户未主动暂停且当前没有爬虫在跑时，才启动冷区后台慢爬
            if (!_userPausedCrawler && !HotReactionsService.Current.IsCrawlerRunning(_chatId))
            {
                HotReactionsService.Current.StartColdCrawler(_clientService, _chatId, OnCrawlerProgress);
            }
        }

        private void OnPopupUnloaded(object sender, RoutedEventArgs e)
        {
            // 弹窗关闭后允许后台慢爬继续静默跑，服务层自会节流
        }

        private async Task RefreshDataAsync()
        {
            LoadingRing.IsActive = true;
            LoadingRing.Visibility = Visibility.Visible;
            EmptyNotice.Visibility = Visibility.Collapsed;

            try
            {
                await HotReactionsService.Current.SyncHotWindowAsync(_clientService, _chatId, status =>
                {
                    var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        CrawlerStatusText.Text = status;
                    });
                });

                UpdateThresholdUI();
                ReloadList(resetPage: true);
            }
            catch (Exception ex)
            {
                Telegram.Logger.Exception(ex);
            }
            finally
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateThresholdUI()
        {
            var state = HotReactionsService.Current.Database.GetSyncState(_chatId);

            if (state.CustomThreshold.HasValue)
            {
                ThresholdInfoText.Text = $"门槛: {state.CustomThreshold.Value} (用户自定义)";
                ThresholdInput.Text = state.CustomThreshold.Value.ToString();
            }
            else
            {
                if (state.SampleCount >= 50 || state.ColdSyncCompleted)
                {
                    ThresholdInfoText.Text = $"门槛: {state.ComputedThreshold} (自适应 90% 均值)";
                }
                else
                {
                    ThresholdInfoText.Text = $"门槛: {state.ComputedThreshold} (采样中 {state.SampleCount}/50)";
                }
                ThresholdInput.Text = string.Empty;
            }
        }

        private void ReloadList(bool resetPage = false)
        {
            if (resetPage)
            {
                _currentPage = 1;
            }

            var state = HotReactionsService.Current.Database.GetSyncState(_chatId);
            long? minDate = _isWeekOnly ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (7 * 86400) : null;

            // limit = 0 取出满足门槛的所有高赞条目，进行全局排序与情绪打分
            _allCurrentItems = HotReactionsService.Current.Database.GetTopMessages(_chatId, 0, minDate, state.EffectiveThreshold, _rankMode);

            int totalCount = _allCurrentItems.Count;
            int totalPages = Math.Max(1, (int)Math.Ceiling((double)totalCount / PageSize));

            if (_currentPage > totalPages)
            {
                _currentPage = totalPages;
            }
            if (_currentPage < 1)
            {
                _currentPage = 1;
            }

            var pageItems = _allCurrentItems.Skip((_currentPage - 1) * PageSize).Take(PageSize).ToList();
            MessagesList.ItemsSource = pageItems;

            EmptyNotice.Visibility = totalCount == 0 ? Visibility.Visible : Visibility.Collapsed;

            // 更新分页条显示
            PaginationSummaryText.Text = $"共 {totalCount} 条热门神帖";
            PageNumText.Text = totalCount > 0 ? $"{_currentPage} / {totalPages}" : "0 / 0";
            PrevPageBtn.IsEnabled = _currentPage > 1;
            NextPageBtn.IsEnabled = _currentPage < totalPages;
        }

        private void PrevPage_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage > 1)
            {
                _currentPage--;
                ReloadList(resetPage: false);
                ScrollListToTop();
            }
        }

        private void NextPage_Click(object sender, RoutedEventArgs e)
        {
            int totalPages = Math.Max(1, (int)Math.Ceiling((double)_allCurrentItems.Count / PageSize));
            if (_currentPage < totalPages)
            {
                _currentPage++;
                ReloadList(resetPage: false);
                ScrollListToTop();
            }
        }

        private void ScrollListToTop()
        {
            if (MessagesList.Items.Count > 0)
            {
                MessagesList.ScrollIntoView(MessagesList.Items[0]);
            }
        }

        private void UpdateRankModeButtons()
        {
            RankNetPosBtn.FontWeight = _rankMode == HotRankMode.NetPositive ? Windows.UI.Text.FontWeights.Bold : Windows.UI.Text.FontWeights.Normal;
            RankPosBtn.FontWeight = _rankMode == HotRankMode.Positive ? Windows.UI.Text.FontWeights.Bold : Windows.UI.Text.FontWeights.Normal;
            RankNegBtn.FontWeight = _rankMode == HotRankMode.Negative ? Windows.UI.Text.FontWeights.Bold : Windows.UI.Text.FontWeights.Normal;
            RankShockBtn.FontWeight = _rankMode == HotRankMode.Shock ? Windows.UI.Text.FontWeights.Bold : Windows.UI.Text.FontWeights.Normal;
            RankAllBtn.FontWeight = _rankMode == HotRankMode.All ? Windows.UI.Text.FontWeights.Bold : Windows.UI.Text.FontWeights.Normal;
        }

        private void RankNetPos_Click(object sender, RoutedEventArgs e)
        {
            if (_rankMode != HotRankMode.NetPositive)
            {
                _rankMode = HotRankMode.NetPositive;
                UpdateRankModeButtons();
                ReloadList(resetPage: true);
            }
        }

        private void RankPos_Click(object sender, RoutedEventArgs e)
        {
            if (_rankMode != HotRankMode.Positive)
            {
                _rankMode = HotRankMode.Positive;
                UpdateRankModeButtons();
                ReloadList(resetPage: true);
            }
        }

        private void RankNeg_Click(object sender, RoutedEventArgs e)
        {
            if (_rankMode != HotRankMode.Negative)
            {
                _rankMode = HotRankMode.Negative;
                UpdateRankModeButtons();
                ReloadList(resetPage: true);
            }
        }

        private void RankShock_Click(object sender, RoutedEventArgs e)
        {
            if (_rankMode != HotRankMode.Shock)
            {
                _rankMode = HotRankMode.Shock;
                UpdateRankModeButtons();
                ReloadList(resetPage: true);
            }
        }

        private void RankAll_Click(object sender, RoutedEventArgs e)
        {
            if (_rankMode != HotRankMode.All)
            {
                _rankMode = HotRankMode.All;
                UpdateRankModeButtons();
                ReloadList(resetPage: true);
            }
        }

        private void SaveConfigEmoji_Click(object sender, RoutedEventArgs e)
        {
            var emoji = ConfigEmojiInput.Text?.Trim();
            if (string.IsNullOrEmpty(emoji)) return;

            int catIndex = ConfigCategorySelect.SelectedIndex;
            if (catIndex >= 0 && catIndex <= 2)
            {
                var category = (SentimentCategory)catIndex;
                ReactionSentimentService.Current.SetCustomCategory(emoji, category);
                HotReactionsService.Current.Database.SaveSentimentConfig(emoji, category);

                ConfigEmojiInput.Text = string.Empty;
                ReloadList(resetPage: false);
            }
        }

        private void ResetConfigEmoji_Click(object sender, RoutedEventArgs e)
        {
            ReactionSentimentService.Current.ResetToDefaults();
            HotReactionsService.Current.Database.ClearSentimentConfigs();
            ConfigEmojiInput.Text = string.Empty;
            ReloadList(resetPage: false);
        }

        private void UpdateFilterButtons()
        {
            WeekFilterBtn.FontWeight = _isWeekOnly ? Windows.UI.Text.FontWeights.Bold : Windows.UI.Text.FontWeights.Normal;
            AllFilterBtn.FontWeight = !_isWeekOnly ? Windows.UI.Text.FontWeights.Bold : Windows.UI.Text.FontWeights.Normal;
        }

        private void FilterWeek_Click(object sender, RoutedEventArgs e)
        {
            if (!_isWeekOnly)
            {
                _isWeekOnly = true;
                UpdateFilterButtons();
                ReloadList(resetPage: true);
            }
        }

        private void FilterAll_Click(object sender, RoutedEventArgs e)
        {
            if (_isWeekOnly)
            {
                _isWeekOnly = false;
                UpdateFilterButtons();
                ReloadList(resetPage: true);

                if (!_userPausedCrawler && !HotReactionsService.Current.IsCrawlerRunning(_chatId))
                {
                    HotReactionsService.Current.StartColdCrawler(_clientService, _chatId, OnCrawlerProgress);
                }
            }
        }

        private void SetThreshold_Click(object sender, RoutedEventArgs e)
        {
            var input = ThresholdInput.Text?.Trim();
            if (string.IsNullOrEmpty(input))
            {
                // 清空表示恢复自适应门槛
                HotReactionsService.Current.SetUserCustomThreshold(_chatId, null);
            }
            else if (int.TryParse(input, out int customVal) && customVal >= 1)
            {
                HotReactionsService.Current.SetUserCustomThreshold(_chatId, customVal);
            }

            UpdateThresholdUI();
            ReloadList(resetPage: true);
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshDataAsync();
        }

        private async void ResetAndRescan_Click(object sender, RoutedEventArgs e)
        {
            _userPausedCrawler = false;
            HotReactionsService.Current.ResetChannelSync(_chatId);
            CrawlerStatusText.Text = "已重置频道历史索引，重新全量扫描...";
            UpdateThresholdUI();
            ReloadList(resetPage: true);
            await RefreshDataAsync();
            if (!_userPausedCrawler)
            {
                HotReactionsService.Current.StartColdCrawler(_clientService, _chatId, OnCrawlerProgress, forceResume: true);
            }
        }

        private void CrawlerToggle_Click(object sender, RoutedEventArgs e)
        {
            bool isRunning = HotReactionsService.Current.IsCrawlerRunning(_chatId);
            if (isRunning)
            {
                _userPausedCrawler = true;
                HotReactionsService.Current.StopColdCrawler(_chatId);
                CrawlerToggleBtn.Content = "继续慢爬";
                CrawlerStatusText.Text = "慢爬已暂停";
            }
            else
            {
                _userPausedCrawler = false;
                HotReactionsService.Current.StartColdCrawler(_clientService, _chatId, OnCrawlerProgress, forceResume: true);
                CrawlerToggleBtn.Content = "暂停慢爬";
                CrawlerStatusText.Text = "慢爬已启动...";
            }
        }

        private void OnCrawlerProgress(string status, bool isRunning)
        {
            var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                if (_userPausedCrawler && isRunning)
                {
                    // 若用户已手动暂停，丢弃滞后的运行态通知，防止覆写 UI 状态
                    return;
                }

                CrawlerStatusText.Text = status;
                CrawlerToggleBtn.Content = isRunning ? "暂停慢爬" : "继续慢爬";

                UpdateThresholdUI();
                ReloadList(resetPage: false);
            });
        }

        private void OnMessageClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is HotMessageItem item)
            {
                Hide();
                _navigationService.NavigateToChat(item.ChatId, item.MessageId);
            }
        }

        private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            Hide();
        }
    }
}
