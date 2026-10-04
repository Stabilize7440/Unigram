//
// Copyright (c) Fela Ameghino 2015-2026
//
// Distributed under the GNU General Public License v3.0. (See accompanying
// file LICENSE or copy at https://www.gnu.org/licenses/gpl-3.0.txt)
//

using System.Collections.Generic;
using Telegram.Controls;
using Telegram.Services.Settings;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Telegram.Views.Popups
{
    public sealed class ChannelContentFilterPopup : ContentPopup
    {
        private readonly ToggleSwitch _enabled;
        private readonly StackPanel _rules = new() { Spacing = 12 };
        private readonly List<(Grid Root, CheckBox Enabled, TextBox Pattern)> _editors = new();
        private readonly TextBlock _error;
        private readonly Button _add;

        public ChannelContentFilterPopup(ChannelContentFilterConfiguration configuration)
        {
            Title = "频道内容过滤";
            PrimaryButtonText = "保存";
            CloseButtonText = "取消";

            _enabled = new ToggleSwitch
            {
                Header = "启用本频道的正则过滤",
                IsOn = configuration.IsEnabled
            };
            _error = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Windows.UI.Colors.IndianRed),
                Visibility = Visibility.Collapsed
            };
            _add = new Button { Content = "添加规则", HorizontalAlignment = HorizontalAlignment.Left };
            _add.Click += (s, e) => AddRule(new ContentFilterRule());

            var content = new StackPanel { Spacing = 16, MinWidth = 320, MaxWidth = 460 };
            content.Children.Add(_enabled);
            content.Children.Add(new TextBlock
            {
                Text = "匹配消息正文及媒体说明；任一启用规则命中就折叠，可点击占位查看原文。仅保存在本账号、本频道。\n默认区分大小写；(?i) 忽略大小写，(?s) 让 . 匹配换行。耗时正则会临时停用，保存后重新启用。",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.75
            });
            content.Children.Add(_rules);
            content.Children.Add(_add);
            content.Children.Add(_error);
            Content = new ScrollViewer
            {
                Content = content,
                MaxHeight = 480,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            foreach (var rule in configuration.Rules)
            {
                AddRule(rule);
            }
            if (configuration.Rules.Count == 0)
            {
                AddRule(new ContentFilterRule());
            }

            PrimaryButtonClick += OnPrimaryButtonClick;
        }

        public ChannelContentFilterConfiguration Configuration { get; private set; }

        private void AddRule(ContentFilterRule rule)
        {
            if (_editors.Count >= ContentFilterSettings.MaxRuleCount)
            {
                return;
            }

            var enabled = new CheckBox
            {
                IsChecked = rule.IsEnabled,
                Content = "启用",
                VerticalAlignment = VerticalAlignment.Top
            };
            var pattern = new TextBox
            {
                Text = rule.Pattern,
                PlaceholderText = "例如：(?i)推广|广告合作|example\\.com",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MaxLength = ContentFilterSettings.MaxPatternLength,
                MinHeight = 52,
                MaxHeight = 120
            };
            var remove = new Button
            {
                Content = "删除",
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(8, 0, 0, 0)
            };
            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Children.Add(enabled);
            root.Children.Add(pattern);
            root.Children.Add(remove);
            Grid.SetRow(pattern, 1);
            Grid.SetColumnSpan(pattern, 2);
            Grid.SetColumn(remove, 1);

            var editor = (root, enabled, pattern);
            _editors.Add(editor);
            _rules.Children.Add(root);
            remove.Click += (s, e) =>
            {
                _editors.Remove(editor);
                _rules.Children.Remove(root);
                _add.IsEnabled = true;
            };
            _add.IsEnabled = _editors.Count < ContentFilterSettings.MaxRuleCount;
        }

        private void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            var configuration = new ChannelContentFilterConfiguration { IsEnabled = _enabled.IsOn };
            for (int i = 0; i < _editors.Count; i++)
            {
                var editor = _editors[i];
                if (string.IsNullOrEmpty(editor.Pattern.Text))
                {
                    continue;
                }

                if (editor.Enabled.IsChecked == true && !ContentFilterSettings.TryValidate(editor.Pattern.Text, out var error))
                {
                    _error.Text = $"第 {i + 1} 条规则无效：{error}";
                    _error.Visibility = Visibility.Visible;
                    editor.Pattern.Focus(FocusState.Programmatic);
                    args.Cancel = true;
                    return;
                }

                configuration.Rules.Add(new ContentFilterRule
                {
                    Pattern = editor.Pattern.Text,
                    IsEnabled = editor.Enabled.IsChecked == true
                });
            }

            Configuration = configuration;
        }
    }
}
