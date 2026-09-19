using System;
using SshTool.App.Controls;
using SshTool.App.ViewModels;
using SshTool.Core.Hosts;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;

namespace SshTool.App.Views.Main
{
    public sealed partial class HostsPivot : UserControl
    {
        public HostsPivot()
        {
            this.InitializeComponent();
        }

        public HostListViewModel ViewModel { get; private set; }

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

        private bool _searchFocused;

        private void OnVmPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            UpdateChrome();
        }

        private void UpdateChrome()
        {
            if (ViewModel == null)
            {
                return;
            }
            SearchBox.Visibility = ViewModel.IsSearchOpen ? Visibility.Visible : Visibility.Collapsed;
            QuickConnectCard.Visibility = ViewModel.ShowQuickConnect ? Visibility.Visible : Visibility.Collapsed;
            Empty.Visibility = ViewModel.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
            NoMatches.Visibility = ViewModel.HasNoMatches ? Visibility.Visible : Visibility.Collapsed;
            HostList.Visibility = ViewModel.IsListVisible ? Visibility.Visible : Visibility.Collapsed;
            if (ViewModel.HasNoMatches)
            {
                NoMatches.Title = "没有匹配“" + ViewModel.SearchText + "”的主机";
                NoMatches.PrimaryText = ViewModel.CanQuickConnectFromSearch ? "用它快速连接" : null;
            }
            else
            {
                NoMatches.PrimaryText = null;
            }
            if (ViewModel.IsSearchOpen)
            {
                if (!_searchFocused)
                {
                    SearchBox.Focus(FocusState.Programmatic);
                    _searchFocused = true;
                }
            }
            else
            {
                _searchFocused = false;
                SearchBox.Text = string.Empty;
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
            if (ViewModel != null)
            {
                ViewModel.Connect(RowOf(sender));
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
                var ignore = ViewModel.DuplicateAsync(RowOf(sender));
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
