using System;
using SshTool.App.Controls;
using SshTool.App.ViewModels;
using SshTool.Core.Hosts;
using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;

namespace SshTool.App.Views.Main
{
    public sealed partial class HostsPivot : UserControl
    {
        private static readonly ResourceLoader Loader = ResourceLoader.GetForCurrentView();

        public HostsPivot()
        {
            this.InitializeComponent();
        }

        public HostListViewModel ViewModel { get; private set; }

        // U12：宽屏主从布局时由 MainPage 赋值：主机行「连接」改为在右侧工作区开标签，
        // 不跳页。返回 true 表示已接管，ViewModel.Connect 不再导航到 TerminalPage。
        public System.Func<HostListRow, bool> WorkspaceOpen { get; set; }

        public void Attach(HostListViewModel viewModel)
        {
            if (ViewModel != null)
            {
                ViewModel.PropertyChanged -= OnVmPropertyChanged;
            }
            ViewModel = viewModel;
            if (ViewModel == null)
            {
                return;
            }
            Empty.PrimaryCommand = ViewModel.NewHostCommand;
            Empty.SecondaryCommand = ViewModel.SignInCommand;
            NoMatches.PrimaryCommand = ViewModel.QuickConnectFromSearchCommand;
            GroupedHosts.Source = ViewModel.Groups;
            HostList.ItemsSource = GroupedHosts.View;
            ViewModel.PropertyChanged += OnVmPropertyChanged;
            UpdateChrome();
        }

        private void OnVmPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            UpdateChrome();
        }

        // V02：搜索行常驻，不再按 IsSearchOpen 显隐/抢焦点；快速连接折叠行 ↔ 表单两态。
        private void UpdateChrome()
        {
            if (ViewModel == null)
            {
                return;
            }
            bool showQuick = ViewModel.ShowQuickConnect;
            QuickConnectRow.Visibility = showQuick ? Visibility.Visible : Visibility.Collapsed;
            QuickConnectCard.Visibility = showQuick && ViewModel.QuickConnectExpanded
                ? Visibility.Visible : Visibility.Collapsed;
            QuickConnectChevron.Glyph = (string)Application.Current.Resources[
                ViewModel.QuickConnectExpanded ? "IconChevronUp" : "IconChevronDown"];
            Empty.Visibility = ViewModel.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
            NoMatches.Visibility = ViewModel.HasNoMatches ? Visibility.Visible : Visibility.Collapsed;
            HostList.Visibility = ViewModel.IsListVisible ? Visibility.Visible : Visibility.Collapsed;
            if (ViewModel.HasNoMatches)
            {
                NoMatches.Title = string.Format(Loader.GetString("Hosts_NoMatchesTitle"), ViewModel.SearchText);
                NoMatches.PrimaryText = ViewModel.CanQuickConnectFromSearch
                    ? Loader.GetString("Hosts_NoMatchesPrimary") : null;
            }
            else
            {
                NoMatches.PrimaryText = null;
            }
            // VM 侧清空搜索词（如 back）时同步回搜索框；程序赋值不触发 UserInput 分支，不会回环。
            if (!string.Equals(SearchBox.Text, ViewModel.SearchText, StringComparison.Ordinal))
            {
                SearchBox.Text = ViewModel.SearchText ?? string.Empty;
            }
        }

        private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (ViewModel == null || args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            {
                return;
            }
            ViewModel.SearchText = sender.Text;
        }

        // 搜索行右侧 ＋：与底栏「新建主机」同一命令。
        private void OnNewHostClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null && ViewModel.NewHostCommand.CanExecute(null))
            {
                ViewModel.NewHostCommand.Execute(null);
            }
        }

        // 折叠行点击 = 底栏「快速连接」命令：切换展开/折叠（态记忆在设置里）。
        private void OnQuickConnectRowTapped(object sender, TappedRoutedEventArgs e)
        {
            if (ViewModel != null && ViewModel.ToggleQuickConnectCommand.CanExecute(null))
            {
                ViewModel.ToggleQuickConnectCommand.Execute(null);
            }
            e.Handled = true;
        }

        private void OnQuickConnectTextChanged(object sender, TextChangedEventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.QuickConnectText = QuickConnectBox.Text;
            }
        }

        private void OnQuickConnectClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null && ViewModel.QuickConnectCommand.CanExecute(null))
            {
                ViewModel.QuickConnectCommand.Execute(null);
            }
        }

        private void OnGroupHeaderTapped(object sender, TappedRoutedEventArgs e)
        {
            if (ViewModel == null)
            {
                return;
            }
            HostListGroup group = ResolveGroup(sender);
            if (group != null)
            {
                ViewModel.ToggleGroup(group.GroupId);
                e.Handled = true;
            }
        }

        private static HostListGroup ResolveGroup(object sender)
        {
            var header = sender as GroupHeader;
            if (header != null && header.Group != null)
            {
                return header.Group;
            }
            var fe = sender as FrameworkElement;
            if (fe == null)
            {
                return null;
            }
            var direct = fe.DataContext as HostListGroup;
            if (direct != null)
            {
                return direct;
            }
            var cvg = fe.DataContext as ICollectionViewGroup;
            return cvg != null ? cvg.Group as HostListGroup : null;
        }

        private static HostListRow RowOf(object sender)
        {
            var row = sender as HostRow;
            return row != null ? row.Row : null;
        }

        private void OnConnectRequested(object sender, EventArgs e)
        {
            HostListRow row = RowOf(sender);
            System.Func<HostListRow, bool> open = WorkspaceOpen;
            if (open != null && row != null && open(row))
            {
                return;
            }
            if (ViewModel != null)
            {
                ViewModel.Connect(row);
            }
        }

        private void OnNewSessionRequested(object sender, EventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.NewSession(RowOf(sender));
            }
        }

        private void OnEditRequested(object sender, EventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.Edit(RowOf(sender));
            }
        }

        private void OnDuplicateRequested(object sender, EventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.Duplicate(RowOf(sender));
            }
        }

        private void OnSftpRequested(object sender, EventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.OpenSftp(RowOf(sender));
            }
        }

        private void OnDeleteRequested(object sender, EventArgs e)
        {
            if (ViewModel != null)
            {
                var ignore = ViewModel.DeleteAsync(RowOf(sender));
            }
        }
    }
}
