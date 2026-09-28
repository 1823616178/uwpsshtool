using System;
using SshTool.App.Infrastructure;
using SshTool.Core.Common;
using SshTool.App.ViewModels.Snippets;
using SshTool.Core.Models;
using SshTool.Core.Sessions;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;

namespace SshTool.App.Controls
{
    // U13：终端内片段选择器。终端菜单 / 键条 snippets 键 / 右键菜单共用入口：
    // SnippetPickerFlyout.Show(anchor, session)。
    public sealed partial class SnippetPickerFlyout : UserControl
    {
        private SessionInfo _session;

        public SnippetPickerFlyout()
        {
            ViewModel = new SnippetPickerViewModel(AppServices.Current);
            this.InitializeComponent();
            GroupedSnippets.Source = ViewModel.Groups;
            SnippetList.ItemsSource = GroupedSnippets.View;
            ViewModel.PropertyChanged += OnVmPropertyChanged;
            UpdateEmpty();
        }

        public SnippetPickerViewModel ViewModel { get; private set; }

        public event EventHandler DismissRequested;

        // U13：返回创建的 Flyout，调用方可挂 Closed（C-03：TerminalPage 借此在选择器
        // 关闭后恢复哨兵焦点）。anchor 为空时返回 null。
        public static Flyout Show(FrameworkElement anchor, SessionInfo session)
        {
            if (anchor == null)
            {
                return null;
            }
            var picker = new SnippetPickerFlyout();
            picker.Attach(session);
            var flyout = new Flyout
            {
                Content = picker,
                Placement = FlyoutPlacementMode.Bottom
            };
            picker.DismissRequested += (s, e) =>
            {
                try
                {
                    flyout.Hide();
                }
                catch (Exception)
                {
                }
            };
            try
            {
                flyout.ShowAt(anchor);
            }
            catch (Exception)
            {
            }
            return flyout;
        }

        public void Attach(SessionInfo session)
        {
            _session = session;
            ViewModel.RefreshAsync().Forget("SnippetPickerFlyout.Refresh", AppLog.Logger);
        }

        private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                ViewModel.Search = sender.Text;
                UpdateEmpty();
            }
        }

        private void OnVmPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "HasNoMatches" || string.IsNullOrEmpty(e.PropertyName))
            {
                UpdateEmpty();
            }
        }

        private async void OnItemClick(object sender, ItemClickEventArgs e)
        {
            try
            {
                var row = e.ClickedItem as SnippetRowVm;
                if (row == null || row.Source == null)
                {
                    return;
                }
                Snippet snippet = row.Source;
                SessionInfo session = _session;
                if (session == null || session.NativeSession == null)
                {
                    return;
                }
                bool sent = await SnippetSendHelper.SendWithPromptsAsync(
                    snippet, session, AppServices.Current.Logger).ConfigureAwait(true);
                if (sent)
                {
                    EventHandler handler = DismissRequested;
                    if (handler != null)
                    {
                        handler(this, EventArgs.Empty);
                    }
                }
                await ViewModel.RefreshAsync().ConfigureAwait(true);
                UpdateEmpty();
            }
            catch (Exception ex)
            {
                AppLog.Error("SnippetPickerFlyout", "OnItemClick failed", ex);
            }
        }

        private void UpdateEmpty()
        {
            Empty.Visibility = ViewModel.HasNoMatches ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
