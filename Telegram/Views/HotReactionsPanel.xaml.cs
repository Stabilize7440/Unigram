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
using Telegram.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Telegram.Views
{
    public sealed partial class HotReactionsPanel : UserControl
    {
        private IClientService _clientService;
        private INavigationService _navigationService;
        private DialogViewModel _dialogViewModel;
        private long _chatId;

        private bool _isWeekOnly = true;
        private bool _userPausedCrawler = false;
        private HotRankMode _rankMode = HotRankMode.NetPositive;

        private int _currentPage = 1;
        private const int PageSize = 40;
        private List<HotMessageItem> _allCurrentItems = new List<HotMessageItem>();
        private System.Threading.CancellationTokenSource _panelCts;
        private int _loadVersion = 0;

        private static void HotLog(string msg)
        {
            try
            {
                var dir = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "hot_debug.log"), $"[{DateTime.Now:HH:mm:ss.fff}] [Panel] {msg}\r\n");
            }
            catch { }
        }

        public event EventHandler CloseRequested;

        public HotReactionsPanel()
        {
            HotLog("HotReactionsPanel ctor starting...");
            try
            {
                InitializeComponent();
                HotLog("HotReactionsPanel InitializeComponent succeeded!");
            }
            catch (Exception ex)
            {
                HotLog($"HotReactionsPanel InitializeComponent EXCEPTION: {ex}");
                Telegram.Logger.Exception(ex);
                throw;
            }
        }

        public void Initialize(IClientService clientService, INavigationService navigationService, DialogViewModel dialogViewModel)
        {
            _clientService = clientService;
            _navigationService = navigationService;
            _dialogViewModel = dialogViewModel;
        }

        public async Task OpenAsync(long chatId)
        {
            HotLog($"OpenAsync called: chatId={chatId}, currentChatId={_chatId}, Visibility={Visibility}");
            if (_chatId == chatId && Visibility == Visibility.Visible && _panelCts != null && !_panelCts.IsCancellationRequested)
            {
                HotLog("OpenAsync: already open for same chat, returning.");
                return;
            }

            // 先清理之前的任务与回调
            try
            {
                _panelCts?.Cancel();
                _panelCts?.Dispose();
            }
            catch { }
            _panelCts = null;

            if (_chatId != 0 && _chatId != chatId)
            {
                HotReactionsService.Current.SetCrawlerProgressCallback(_chatId, null);
            }

            if (_chatId != chatId)
            {
                _userPausedCrawler = false;
                CrawlerToggleBtn.Content = "暂停慢爬";
                CrawlerStatusText.Text = "冷区同步准备中...";
            }

            _chatId = chatId;
            Visibility = Visibility.Visible;

            _allCurrentItems.Clear();
            MessagesList.ItemsSource = null;
            PaginationSummaryText.Text = "加载中...";
            EmptyNotice.Visibility = Visibility.Collapsed;
            LoadingRing.IsActive = true;
            LoadingRing.Visibility = Visibility.Visible;

            UpdateFilterButtons();
            UpdateRankModeButtons();

            _panelCts = new System.Threading.CancellationTokenSource();
            var boundCts = _panelCts;
            var token = boundCts.Token;
            var targetChatId = chatId;

            try
            {
                // 1. 注册慢爬进度监听（若后台慢爬正在运行，立刻无缝接管）
                HotReactionsService.Current.SetCrawlerProgressCallback(targetChatId, (status, isRunning) =>
                {
                    OnCrawlerProgress(status, isRunning, boundCts, targetChatId);
                });

                // 2. 秒开：优先呈现本地已有索引，杜绝空白等待
                HotLog("OpenAsync: calling ReloadListAsync");
                await ReloadListAsync(resetPage: true, token: token);
                if (token.IsCancellationRequested) return;

                // 3. 静默增量同步近7天热区消息
                HotLog("OpenAsync: calling RefreshDataAsync");
                await RefreshDataAsync(token);
                if (token.IsCancellationRequested) return;

                // 4. 仅在用户未主动暂停且当前没有爬虫在跑时，才启动冷区后台慢爬
                if (!_userPausedCrawler && !token.IsCancellationRequested && !HotReactionsService.Current.IsCrawlerRunning(targetChatId))
                {
                    HotLog("OpenAsync: starting cold crawler");
                    HotReactionsService.Current.StartColdCrawler(_clientService, targetChatId, (status, isRunning) =>
                    {
                        OnCrawlerProgress(status, isRunning, boundCts, targetChatId);
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // 正常取消
            }
            catch (Exception ex)
            {
                HotLog($"OpenAsync EXCEPTION: {ex}");
                Telegram.Logger.Exception(ex);
                if (boundCts == _panelCts && !token.IsCancellationRequested)
                {
                    PaginationSummaryText.Text = "加载失败，请重试";
                }
            }
            finally
            {
                if (boundCts == _panelCts && !token.IsCancellationRequested)
                {
                    LoadingRing.IsActive = false;
                    LoadingRing.Visibility = Visibility.Collapsed;
                }
            }
        }

        public void Close()
        {
            try
            {
                _panelCts?.Cancel();
                _panelCts?.Dispose();
            }
            catch { }
            _panelCts = null;

            if (_chatId != 0)
            {
                HotReactionsService.Current.SetCrawlerProgressCallback(_chatId, null);
            }

            Visibility = Visibility.Collapsed;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        private async Task RefreshDataAsync(System.Threading.CancellationToken token = default)
        {
            if (token.IsCancellationRequested) return;

            if (_allCurrentItems.Count == 0)
            {
                LoadingRing.IsActive = true;
                LoadingRing.Visibility = Visibility.Visible;
                EmptyNotice.Visibility = Visibility.Collapsed;
            }

            try
            {
                await HotReactionsService.Current.SyncHotWindowAsync(_clientService, _chatId, status =>
                {
                    if (token.IsCancellationRequested) return;
                    var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        if (token.IsCancellationRequested) return;
                        CrawlerStatusText.Text = status;
                    });
                });

                if (token.IsCancellationRequested) return;
                await ReloadListAsync(resetPage: true, token: token);
            }
            catch (Exception ex)
            {
                Telegram.Logger.Exception(ex);
            }
            finally
            {
                if (!token.IsCancellationRequested && _panelCts != null && !_panelCts.IsCancellationRequested)
                {
                    LoadingRing.IsActive = false;
                    LoadingRing.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void UpdateThresholdUI(ChannelSyncState state = null)
        {
            state ??= HotReactionsService.Current.Database.GetSyncState(_chatId);

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

        private async Task ReloadListAsync(bool resetPage = false, System.Threading.CancellationToken token = default)
        {
            int version = System.Threading.Interlocked.Increment(ref _loadVersion);

            if (resetPage)
            {
                _currentPage = 1;
            }

            var chatId = _chatId;
            var isWeekOnly = _isWeekOnly;
            var rankMode = _rankMode;

            // 数据库读取、JSON解析与情绪重排全部移至后台线程
            var (items, state) = await Task.Run(() =>
            {
                var syncState = HotReactionsService.Current.Database.GetSyncState(chatId);
                long? minDate = isWeekOnly ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (7 * 86400) : null;
                var topItems = HotReactionsService.Current.Database.GetTopMessages(chatId, 0, minDate, syncState.EffectiveThreshold, rankMode);
                return (topItems, syncState);
            });

            if (version != _loadVersion || token.IsCancellationRequested || _panelCts == null || _panelCts.IsCancellationRequested)
            {
                return;
            }

            _allCurrentItems = items;
            UpdateThresholdUI(state);
            RenderCurrentPage();
            if (items.Count > 0)
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
            }
            HotLog($"ReloadListAsync finished: count={items.Count}, effectiveThreshold={state?.EffectiveThreshold}");
        }

        private void RenderCurrentPage()
        {
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
                RenderCurrentPage();
                ScrollListToTop();
            }
        }

        private void NextPage_Click(object sender, RoutedEventArgs e)
        {
            int totalPages = Math.Max(1, (int)Math.Ceiling((double)_allCurrentItems.Count / PageSize));
            if (_currentPage < totalPages)
            {
                _currentPage++;
                RenderCurrentPage();
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
                var boundCts = _panelCts;
                _ = ReloadListAsync(resetPage: true, token: boundCts?.Token ?? default);
            }
        }

        private void RankPos_Click(object sender, RoutedEventArgs e)
        {
            if (_rankMode != HotRankMode.Positive)
            {
                _rankMode = HotRankMode.Positive;
                UpdateRankModeButtons();
                var boundCts = _panelCts;
                _ = ReloadListAsync(resetPage: true, token: boundCts?.Token ?? default);
            }
        }

        private void RankNeg_Click(object sender, RoutedEventArgs e)
        {
            if (_rankMode != HotRankMode.Negative)
            {
                _rankMode = HotRankMode.Negative;
                UpdateRankModeButtons();
                var boundCts = _panelCts;
                _ = ReloadListAsync(resetPage: true, token: boundCts?.Token ?? default);
            }
        }

        private void RankShock_Click(object sender, RoutedEventArgs e)
        {
            if (_rankMode != HotRankMode.Shock)
            {
                _rankMode = HotRankMode.Shock;
                UpdateRankModeButtons();
                var boundCts = _panelCts;
                _ = ReloadListAsync(resetPage: true, token: boundCts?.Token ?? default);
            }
        }

        private void RankAll_Click(object sender, RoutedEventArgs e)
        {
            if (_rankMode != HotRankMode.All)
            {
                _rankMode = HotRankMode.All;
                UpdateRankModeButtons();
                var boundCts = _panelCts;
                _ = ReloadListAsync(resetPage: true, token: boundCts?.Token ?? default);
            }
        }

        private async void SaveConfigEmoji_Click(object sender, RoutedEventArgs e)
        {
            var emoji = ConfigEmojiInput.Text?.Trim();
            if (string.IsNullOrEmpty(emoji)) return;

            int catIndex = ConfigCategorySelect.SelectedIndex;
            if (catIndex >= 0 && catIndex <= 2)
            {
                var boundCts = _panelCts;
                var token = boundCts?.Token ?? default;
                var category = (SentimentCategory)catIndex;
                ReactionSentimentService.Current.SetCustomCategory(emoji, category);
                ConfigEmojiInput.Text = string.Empty;
                await Task.Run(() => HotReactionsService.Current.Database.SaveSentimentConfig(emoji, category));
                if (token.IsCancellationRequested || boundCts != _panelCts || _panelCts?.IsCancellationRequested == true) return;
                await ReloadListAsync(resetPage: false, token: token);
            }
        }

        private async void ResetConfigEmoji_Click(object sender, RoutedEventArgs e)
        {
            var boundCts = _panelCts;
            var token = boundCts?.Token ?? default;
            ReactionSentimentService.Current.ResetToDefaults();
            ConfigEmojiInput.Text = string.Empty;
            await Task.Run(() => HotReactionsService.Current.Database.ClearSentimentConfigs());
            if (token.IsCancellationRequested || boundCts != _panelCts || _panelCts?.IsCancellationRequested == true) return;
            await ReloadListAsync(resetPage: false, token: token);
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
                var boundCts = _panelCts;
                _ = ReloadListAsync(resetPage: true, token: boundCts?.Token ?? default);
            }
        }

        private void FilterAll_Click(object sender, RoutedEventArgs e)
        {
            if (_isWeekOnly)
            {
                _isWeekOnly = false;
                UpdateFilterButtons();
                var boundCts = _panelCts;
                _ = ReloadListAsync(resetPage: true, token: boundCts?.Token ?? default);
                if (!_userPausedCrawler && !HotReactionsService.Current.IsCrawlerRunning(_chatId))
                {
                    var boundChatId = _chatId;
                    HotReactionsService.Current.StartColdCrawler(_clientService, boundChatId, (status, isRunning) =>
                    {
                        OnCrawlerProgress(status, isRunning, boundCts, boundChatId);
                    });
                }
            }
        }

        private async void SetThreshold_Click(object sender, RoutedEventArgs e)
        {
            var boundCts = _panelCts;
            var token = boundCts?.Token ?? default;
            var input = ThresholdInput.Text?.Trim();
            int? customVal = null;
            if (!string.IsNullOrEmpty(input) && int.TryParse(input, out int parsed) && parsed >= 1)
            {
                customVal = parsed;
            }

            var chatId = _chatId;
            await Task.Run(() => HotReactionsService.Current.SetUserCustomThreshold(chatId, customVal));
            if (token.IsCancellationRequested || boundCts != _panelCts || _panelCts?.IsCancellationRequested == true) return;
            await ReloadListAsync(resetPage: true, token: token);
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshDataAsync(_panelCts?.Token ?? default);
        }

        private async void ResetAndRescan_Click(object sender, RoutedEventArgs e)
        {
            _userPausedCrawler = false;
            var boundCts = _panelCts;
            var token = boundCts?.Token ?? default;
            var chatId = _chatId;
            CrawlerStatusText.Text = "已重置频道历史索引，重新全量扫描...";
            await HotReactionsService.Current.ResetChannelSyncAsync(chatId);
            if (token.IsCancellationRequested || boundCts != _panelCts || _panelCts?.IsCancellationRequested == true) return;
            await ReloadListAsync(resetPage: true, token: token);
            if (token.IsCancellationRequested || boundCts != _panelCts || _panelCts?.IsCancellationRequested == true) return;
            await RefreshDataAsync(token);
            if (!_userPausedCrawler && token.IsCancellationRequested == false && boundCts == _panelCts && _panelCts?.IsCancellationRequested == false)
            {
                var boundChatId = _chatId;
                HotReactionsService.Current.StartColdCrawler(_clientService, boundChatId, (status, isRunning) =>
                {
                    OnCrawlerProgress(status, isRunning, boundCts, boundChatId);
                }, forceResume: true);
            }
        }

        private void CrawlerToggle_Click(object sender, RoutedEventArgs e)
        {
            var boundCts = _panelCts;
            var boundChatId = _chatId;
            bool isRunning = HotReactionsService.Current.IsCrawlerRunning(boundChatId);
            if (isRunning)
            {
                _userPausedCrawler = true;
                HotReactionsService.Current.StopColdCrawler(boundChatId);
                CrawlerToggleBtn.Content = "继续慢爬";
                CrawlerStatusText.Text = "慢爬已暂停";
            }
            else
            {
                _userPausedCrawler = false;
                HotReactionsService.Current.StartColdCrawler(_clientService, boundChatId, (status, isRunning) =>
                {
                    OnCrawlerProgress(status, isRunning, boundCts, boundChatId);
                }, forceResume: true);
                CrawlerToggleBtn.Content = "暂停慢爬";
                CrawlerStatusText.Text = "慢爬已启动...";
            }
        }

        private void OnCrawlerProgress(string status, bool isRunning, System.Threading.CancellationTokenSource boundCts, long boundChatId)
        {
            if (boundCts == null || boundCts.IsCancellationRequested)
            {
                return;
            }

            var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () =>
            {
                if (boundCts != _panelCts || boundCts.IsCancellationRequested || _chatId != boundChatId)
                {
                    return;
                }

                if (_userPausedCrawler && isRunning)
                {
                    return;
                }

                CrawlerStatusText.Text = status;
                CrawlerToggleBtn.Content = isRunning ? "暂停慢爬" : "继续慢爬";

                await ReloadListAsync(resetPage: false, token: boundCts.Token);
            });
        }

        private long _lastClickedMsgId;
        private DateTime _lastClickTime = DateTime.MinValue;

        private void OnMessageClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is HotMessageItem item)
            {
                var now = DateTime.UtcNow;
                if (_lastClickedMsgId == item.MessageId && (now - _lastClickTime).TotalMilliseconds < 500)
                {
                    return;
                }
                _lastClickedMsgId = item.MessageId;
                _lastClickTime = now;

                // 同屏平滑定位：在常规历史流下直接滚动高亮；在论坛话题/线程模式下通过导航服务精确切换
                if (_dialogViewModel != null && _dialogViewModel.ChatId == _chatId && _dialogViewModel.Type == DialogType.History)
                {
                    _ = _dialogViewModel.LoadMessageSliceAsync(null, item.MessageId);
                }
                else
                {
                    _navigationService?.NavigateToChat(item.ChatId, item.MessageId);
                }
            }
        }
    }
}
