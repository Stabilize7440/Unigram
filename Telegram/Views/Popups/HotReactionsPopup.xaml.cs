//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System;
using Telegram.Controls;
using Telegram.Navigation.Services;
using Telegram.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Telegram.Views.Popups
{
    // Compatibility host: ranking, configuration and lifetime behavior live in one control.
    public sealed partial class HotReactionsPopup : ContentPopup
    {
        private readonly long _chatId;

        public HotReactionsPopup(IClientService clientService, INavigationService navigationService, long chatId)
        {
            InitializeComponent();
            _chatId = chatId;
            RankingPanel.Initialize(clientService, navigationService, null);
            RankingPanel.CloseRequested += (sender, args) => Hide();
            RankingPanel.MessageSelected += (sender, args) => Hide();
        }

        private async void OnPopupLoaded(object sender, RoutedEventArgs e)
        {
            try { await RankingPanel.OpenAsync(_chatId); }
            catch (Exception ex) { Logger.Exception(ex); }
        }

        private void OnPopupUnloaded(object sender, RoutedEventArgs e)
        {
            RankingPanel.Close();
        }

        private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            Hide();
        }
    }
}
