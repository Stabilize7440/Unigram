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
        private HotReactionsService _service;
        private Action<long, string, bool> _progressHandler;
        private Action<long> _messagesHandler;
        private Task _reloadTask;
        private Task _refreshTask;
        private bool _reloadQueued;
        private bool _resetPageQueued;
        private long _renderedRevision = -1;

        private static void HotLog(string msg)
        {
            System.Diagnostics.Debug.WriteLine($"[HotReactions] {msg}");
        }

        public event EventHandler CloseRequested;
        public event EventHandler MessageSelected;

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
            var service = clientService.HotReactions;
            if (_clientService != clientService || _service != service) Close();
            _clientService = clientService;
            _service = service;
            _navigationService = navigationService;
            _dialogViewModel = dialogViewModel;
        }

        public async Task OpenAsync(long chatId)
        {
            if (_chatId == chatId && Visibility == Visibility.Visible && _panelCts?.IsCancellationRequested == false) return;
            Close();
            _chatId = chatId;
            Visibility = Visibility.Visible;
            _userPausedCrawler = _service.IsCrawlerPaused(chatId);
            CrawlerToggleBtn.Content = _userPausedCrawler ? "继续慢爬" : "暂停慢爬";
            CrawlerStatusText.Text = _userPausedCrawler ? "慢爬已暂停" : "冷区同步准备中...";
            PaginationSummaryText.Text = "加载中...";
            EmptyNotice.Visibility = Visibility.Collapsed;
            LoadingRing.IsActive = true;
            LoadingRing.Visibility = Visibility.Visible;
            UpdateFilterButtons();
            UpdateRankModeButtons();

            var boundCts = _panelCts = new System.Threading.CancellationTokenSource();
            var token = boundCts.Token;
            _progressHandler = (id, status, running) =>
            {
                if (id == chatId) OnCrawlerProgress(status, running, boundCts, chatId);
            };
            _messagesHandler = id =>
            {
                if (id != chatId || token.IsCancellationRequested) return;
                _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    if (boundCts != _panelCts || token.IsCancellationRequested) return;
                    if (_renderedRevision != _service.Database.GetRevision(id)) _ = ReloadListAsync(token: token);
                });
            };
            _service.CrawlerProgress += _progressHandler;
            _service.MessagesChanged += _messagesHandler;
            try
            {
                await _service.InitializeAsync();
                if (token.IsCancellationRequested) return;
                await ReloadListAsync(resetPage: true, token: token);
                if (token.IsCancellationRequested) return;
                await RefreshDataAsync(token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Telegram.Logger.Exception(ex);
                if (boundCts == _panelCts) PaginationSummaryText.Text = "加载失败，请重试";
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
            if (_service != null)
            {
                _service.CrawlerProgress -= _progressHandler;
                _service.MessagesChanged -= _messagesHandler;
            }
            _progressHandler = null;
            _messagesHandler = null;
            _panelCts?.Cancel();
            _panelCts?.Dispose();
            _panelCts = null;
            _loadVersion++;
            _reloadTask = null;
            _refreshTask = null;
            _reloadQueued = false;
            _resetPageQueued = false;
            _renderedRevision = -1;
            _allCurrentItems.Clear();
            MessagesList.ItemsSource = null;
            Visibility = Visibility.Collapsed;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        private Task RefreshDataAsync(System.Threading.CancellationToken token = default)
        {
            if (token.IsCancellationRequested || _panelCts == null) return Task.CompletedTask;
            if (_refreshTask?.IsCompleted == false) return _refreshTask;
            return _refreshTask = RefreshCoreAsync(token);
        }

        private async Task RefreshCoreAsync(System.Threading.CancellationToken token)
        {
            var service = _service;
            var client = _clientService;
            var chatId = _chatId;
            if (_allCurrentItems.Count == 0)
            {
                LoadingRing.IsActive = true;
                LoadingRing.Visibility = Visibility.Visible;
            }
            try
            {
                await service.SyncHotWindowAsync(client, chatId, status =>
                {
                    if (token.IsCancellationRequested) return;
                    _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        if (!token.IsCancellationRequested) CrawlerStatusText.Text = status;
                    });
                }, token);
                if (!token.IsCancellationRequested) await ReloadListAsync(resetPage: true, token: token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Telegram.Logger.Exception(ex);
                if (!token.IsCancellationRequested) CrawlerStatusText.Text = "同步失败，未跳过失败批次，请重试";
            }
            finally
            {
                if (!token.IsCancellationRequested)
                {
                    LoadingRing.IsActive = false;
                    LoadingRing.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void UpdateThresholdUI(ChannelSyncState state = null)
        {
            state ??= _service.Database.GetSyncState(_chatId);

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

        private Task ReloadListAsync(bool resetPage = false, System.Threading.CancellationToken token = default)
        {
            if (token.IsCancellationRequested || _panelCts == null) return Task.CompletedTask;
            _loadVersion++;
            _reloadQueued = true;
            _resetPageQueued |= resetPage;
            if (_reloadTask?.IsCompleted == false) return _reloadTask;
            return _reloadTask = ReloadLoopAsync(_panelCts, _chatId, _service);
        }

        private async Task ReloadLoopAsync(System.Threading.CancellationTokenSource boundCts, long chatId, HotReactionsService service)
        {
            var token = boundCts.Token;
            try
            {
                while (_reloadQueued && boundCts == _panelCts && !token.IsCancellationRequested)
                {
                    _reloadQueued = false;
                    if (_resetPageQueued) _currentPage = 1;
                    _resetPageQueued = false;
                    int version = _loadVersion;
                    var weekOnly = _isWeekOnly;
                    var mode = _rankMode;
                    var (items, state, revision) = await Task.Run(() =>
                    {
                        var revision = service.Database.GetRevision(chatId);
                        var state = service.Database.GetSyncState(chatId);
                        long? minDate = weekOnly ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 7 * 86400 : null;
                        var items = service.Database.GetTopMessages(chatId, 0, minDate, state.EffectiveThreshold, mode, token);
                        return (items, state, revision);
                    }, token);
                    if (boundCts != _panelCts || token.IsCancellationRequested) return;
                    if (version != _loadVersion) continue;
                    _allCurrentItems = items;
                    _renderedRevision = revision;
                    UpdateThresholdUI(state);
                    RenderCurrentPage();
                    LoadingRing.IsActive = false;
                    LoadingRing.Visibility = Visibility.Collapsed;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Telegram.Logger.Exception(ex);
                if (boundCts == _panelCts) PaginationSummaryText.Text = "加载失败，请重试";
            }
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
            var index = ConfigCategorySelect.SelectedIndex;
            if (string.IsNullOrEmpty(emoji) || emoji == ReactionSentimentService.PaidReactionKey || index < 0 || index > 2) return;
            var boundCts = _panelCts;
            if (boundCts == null) return;
            var token = boundCts.Token;
            var database = _service.Database;
            ConfigEmojiInput.Text = string.Empty;
            try
            {
                await Task.Run(() => database.SaveSentimentConfig(emoji, (SentimentCategory)index));
                if (boundCts == _panelCts && !token.IsCancellationRequested) await ReloadListAsync(token: token);
            }
            catch (Exception ex) { Telegram.Logger.Exception(ex); }
        }

        private async void ResetConfigEmoji_Click(object sender, RoutedEventArgs e)
        {
            var boundCts = _panelCts;
            if (boundCts == null) return;
            var token = boundCts.Token;
            var database = _service.Database;
            try
            {
                await Task.Run(database.ClearSentimentConfigs);
                if (boundCts == _panelCts && !token.IsCancellationRequested) await ReloadListAsync(token: token);
            }
            catch (Exception ex) { Telegram.Logger.Exception(ex); }
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
            if (!_isWeekOnly) return;
            _isWeekOnly = false;
            UpdateFilterButtons();
            _ = ReloadListAsync(resetPage: true, token: _panelCts?.Token ?? default);
            if (!_userPausedCrawler) _service.StartColdCrawler(_clientService, _chatId);
        }

        private async void SetThreshold_Click(object sender, RoutedEventArgs e)
        {
            var input = ThresholdInput.Text?.Trim();
            int? value = null;
            if (!string.IsNullOrEmpty(input))
            {
                if (!int.TryParse(input, out int parsed) || parsed < 1)
                {
                    ThresholdInfoText.Text = "请输入大于 0 的整数；留空恢复自适应门槛";
                    return;
                }
                value = parsed;
            }
            var boundCts = _panelCts;
            if (boundCts == null) return;
            var token = boundCts.Token;
            var service = _service;
            var client = _clientService;
            var chatId = _chatId;
            try
            {
                bool rescan = await Task.Run(() => service.Database.RequiresRescan(chatId, value));
                if (boundCts != _panelCts || token.IsCancellationRequested) return;
                if (rescan)
                {
                    if (!await ConfirmRescanAsync(token)) return;
                    if (boundCts != _panelCts || token.IsCancellationRequested) return;
                }
                bool applied = await Task.Run(() => service.SetUserCustomThreshold(chatId, value, rescan));
                if (!applied)
                {
                    // A concurrent batch may have raised the pruning floor since the first check.
                    if (!await ConfirmRescanAsync(token)) return;
                    if (boundCts != _panelCts || token.IsCancellationRequested) return;
                    rescan = true;
                    await Task.Run(() => service.SetUserCustomThreshold(chatId, value, rescanConfirmed: true));
                }
                if (rescan)
                {
                    await service.ResetChannelSyncAsync(chatId);
                    if (boundCts != _panelCts || token.IsCancellationRequested) return;
                    _userPausedCrawler = false;
                    await RefreshDataAsync(token);
                    if (boundCts == _panelCts && !token.IsCancellationRequested) service.StartColdCrawler(client, chatId);
                }
                if (boundCts == _panelCts && !token.IsCancellationRequested) await ReloadListAsync(resetPage: true, token: token);
            }
            catch (Exception ex) { Telegram.Logger.Exception(ex); }
        }

        private async Task<bool> ConfirmRescanAsync(System.Threading.CancellationToken token)
        {
            // A Flyout also works inside the legacy modal host; queuing another dialog would deadlock it.
            var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var content = new StackPanel { Width = 280, Spacing = 8 };
            content.Children.Add(new TextBlock
            {
                Text = "较低反应数的历史消息已被剪枝。确认后会保留新门槛并从头重扫，后台请求受账号级限速控制。",
                TextWrapping = TextWrapping.Wrap
            });
            var confirm = new Button { Content = "重扫并应用", HorizontalAlignment = HorizontalAlignment.Stretch };
            var cancel = new Button { Content = "取消", HorizontalAlignment = HorizontalAlignment.Stretch };
            content.Children.Add(confirm);
            content.Children.Add(cancel);
            var flyout = new Flyout { Content = content };
            confirm.Click += (sender, args) => { result.TrySetResult(true); flyout.Hide(); };
            cancel.Click += (sender, args) => flyout.Hide();
            flyout.Closed += (sender, args) => result.TrySetResult(false);
            using var registration = token.Register(() =>
            {
                result.TrySetResult(false);
                try { _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, flyout.Hide); }
                catch (Exception ex) { Telegram.Logger.Exception(ex); }
            });
            if (token.IsCancellationRequested) return false;
            flyout.ShowAt(SetThresholdBtn);
            return await result.Task;
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshDataAsync(_panelCts?.Token ?? default);
        }

        private async void ResetAndRescan_Click(object sender, RoutedEventArgs e)
        {
            var boundCts = _panelCts;
            if (boundCts == null) return;
            var token = boundCts.Token;
            var service = _service;
            var client = _clientService;
            var chatId = _chatId;
            try
            {
                await service.ResetChannelSyncAsync(chatId);
                if (boundCts != _panelCts || token.IsCancellationRequested) return;
                _userPausedCrawler = false;
                await ReloadListAsync(resetPage: true, token: token);
                await RefreshDataAsync(token);
                if (boundCts == _panelCts && !token.IsCancellationRequested) service.StartColdCrawler(client, chatId);
            }
            catch (Exception ex) { Telegram.Logger.Exception(ex); }
        }

        private void CrawlerToggle_Click(object sender, RoutedEventArgs e)
        {
            if (_service.IsCrawlerRunning(_chatId))
            {
                _userPausedCrawler = true;
                _ = _service.StopColdCrawler(_chatId);
                CrawlerToggleBtn.Content = "继续慢爬";
                CrawlerStatusText.Text = "慢爬已暂停";
            }
            else
            {
                _userPausedCrawler = false;
                _service.StartColdCrawler(_clientService, _chatId);
                CrawlerToggleBtn.Content = "暂停慢爬";
            }
        }

        private void OnCrawlerProgress(string status, bool isRunning, System.Threading.CancellationTokenSource boundCts, long boundChatId)
        {
            if (boundCts == null || boundCts.IsCancellationRequested) return;
            _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () =>
            {
                if (boundCts != _panelCts || boundCts.IsCancellationRequested || _chatId != boundChatId) return;
                if (_userPausedCrawler && isRunning) return;
                CrawlerStatusText.Text = status;
                CrawlerToggleBtn.Content = isRunning ? "暂停慢爬" : "继续慢爬";
                var service = _service;
                try
                {
                    var state = await Task.Run(() => service.Database.GetSyncState(boundChatId));
                    if (boundCts != _panelCts || boundCts.IsCancellationRequested) return;
                    UpdateThresholdUI(state);
                    if (_renderedRevision != service.Database.GetRevision(boundChatId)) await ReloadListAsync(token: boundCts.Token);
                }
                catch (Exception ex) { Telegram.Logger.Exception(ex); }
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
                MessageSelected?.Invoke(this, EventArgs.Empty);

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
