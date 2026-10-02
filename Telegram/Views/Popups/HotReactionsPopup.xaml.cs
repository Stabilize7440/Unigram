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
            await RefreshDataAsync();

            // 启动冷区后台慢爬
            HotReactionsService.Current.StartColdCrawler(_clientService, _chatId, OnCrawlerProgress);
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
                ReloadList();
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

        private void ReloadList()
        {
            var state = HotReactionsService.Current.Database.GetSyncState(_chatId);
            long? minDate = _isWeekOnly ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (7 * 86400) : null;

            var items = HotReactionsService.Current.Database.GetTopMessages(_chatId, 100, minDate, state.EffectiveThreshold);
            MessagesList.ItemsSource = items;

            EmptyNotice.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
                ReloadList();
            }
        }

        private void FilterAll_Click(object sender, RoutedEventArgs e)
        {
            if (_isWeekOnly)
            {
                _isWeekOnly = false;
                UpdateFilterButtons();
                ReloadList();

                if (!HotReactionsService.Current.IsCrawlerRunning(_chatId))
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
            ReloadList();
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshDataAsync();
        }

        private async void ResetAndRescan_Click(object sender, RoutedEventArgs e)
        {
            HotReactionsService.Current.ResetChannelSync(_chatId);
            CrawlerStatusText.Text = "已重置频道历史索引，重新全量扫描...";
            UpdateThresholdUI();
            ReloadList();
            await RefreshDataAsync();
            HotReactionsService.Current.StartColdCrawler(_clientService, _chatId, OnCrawlerProgress, forceResume: true);
        }

        private void CrawlerToggle_Click(object sender, RoutedEventArgs e)
        {
            if (HotReactionsService.Current.IsCrawlerRunning(_chatId))
            {
                HotReactionsService.Current.StopColdCrawler(_chatId);
                CrawlerToggleBtn.Content = "继续慢爬";
                CrawlerStatusText.Text = "慢爬已暂停";
            }
            else
            {
                HotReactionsService.Current.StartColdCrawler(_clientService, _chatId, OnCrawlerProgress, forceResume: true);
                CrawlerToggleBtn.Content = "暂停慢爬";
                CrawlerStatusText.Text = "慢爬已启动...";
            }
        }

        private void OnCrawlerProgress(string status, bool isRunning)
        {
            var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                CrawlerStatusText.Text = status;
                CrawlerToggleBtn.Content = isRunning ? "暂停慢爬" : "继续慢爬";

                UpdateThresholdUI();
                ReloadList();
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
