using SshTool.App.Infrastructure;
using SshTool.Core.Common;
using SshTool.App.ViewModels.Snippets;
using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views
{
    public sealed class SnippetsArgs
    {
        // 可选：绑定一个会话后行内 ▶ 可用；为空则纯管理（发送禁用）。
        public string SessionId { get; set; }

        public static SnippetsArgs Manage()
        {
            return new SnippetsArgs();
        }

        public static SnippetsArgs WithSession(string sessionId)
        {
            return new SnippetsArgs { SessionId = sessionId };
        }
    }

    public sealed partial class SnippetsPage : Page
    {
        // W06：页头返回按钮（与硬件返回键同一条处理链）。
        private void OnHeaderBackRequested(object sender, System.EventArgs e)
        {
            NavigationService nav;
            if (ServiceRegistry.TryGet(out nav))
            {
                nav.RequestBack();
            }
        }

        private readonly ResourceLoader _loader = ResourceLoader.GetForCurrentView();

        public SnippetsPage()
        {
            ViewModel = new SnippetsViewModel(AppServices.Current);
            this.InitializeComponent();
            GroupedSnippets.Source = ViewModel.Groups;
            SnippetList.ItemsSource = GroupedSnippets.View;
            Empty.PrimaryCommand = ViewModel.NewCommand;
            // V04b（C-06）：EmptyState 文案走 resw 双语。
            Empty.Title = _loader.GetString("Snippets_Empty_Title");
            Empty.Description = _loader.GetString("Snippets_Empty_Description");
            Empty.PrimaryText = _loader.GetString("Snippets_Empty_Primary");
            NoMatches.Title = _loader.GetString("Snippets_NoMatches_Title");
            NoMatches.Description = _loader.GetString("Snippets_NoMatches_Description");
            NoMatches.PrimaryText = _loader.GetString("Common_ClearSearch");
            NoMatches.PrimaryClick += OnClearSearchClick;
            BottomBar.PrimaryText = _loader.GetString("Snippets_New/Label");
        }

        public SnippetsViewModel ViewModel { get; private set; }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            var args = e.Parameter as SnippetsArgs;
            ViewModel.AttachSession(args == null ? null : args.SessionId);
            ViewModel.RefreshAsync().Forget("SnippetsPage.Refresh", AppLog.Logger);
            UpdateChrome();
            ViewModel.PropertyChanged += OnVmPropertyChanged;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            ViewModel.PropertyChanged -= OnVmPropertyChanged;
            base.OnNavigatedFrom(e);
        }

        private void OnVmPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            UpdateChrome();
        }

        private void UpdateChrome()
        {
            bool hasError = ViewModel.HasError;
            ErrorState.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
            Empty.Visibility = (!hasError && ViewModel.IsEmpty) ? Visibility.Visible : Visibility.Collapsed;
            NoMatches.Visibility = (!hasError && ViewModel.HasNoMatches) ? Visibility.Visible : Visibility.Collapsed;
            SnippetList.Visibility =
                (hasError || ViewModel.IsEmpty || ViewModel.HasNoMatches) ? Visibility.Collapsed : Visibility.Visible;
            if (hasError)
            {
                ErrorState.Title = _loader.GetString("Common_LoadFailed");
                ErrorState.Description = ViewModel.ErrorMessage;
                ErrorState.PrimaryText = _loader.GetString("Common_Retry");
                ErrorState.PrimaryClick -= OnRetryClick;
                ErrorState.PrimaryClick += OnRetryClick;
            }
        }

        private void OnClearSearchClick(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = string.Empty;
            ViewModel.Search = string.Empty;
            UpdateChrome();
        }

        private void OnRetryClick(object sender, RoutedEventArgs e)
        {
            ViewModel.RefreshAsync().Forget("SnippetsPage.Retry", AppLog.Logger);
        }

        private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                ViewModel.Search = sender.Text;
                UpdateChrome();
            }
        }

        private void OnNewClick(object sender, System.EventArgs e)
        {
            ViewModel.OpenNew();
        }

        private void OnItemClick(object sender, ItemClickEventArgs e)
        {
            ViewModel.OpenEdit(e.ClickedItem as SnippetRowVm);
        }

        private void OnSendClick(object sender, RoutedEventArgs e)
        {
            var row = (sender as FrameworkElement).DataContext as SnippetRowVm;
            ViewModel.SendAsync(row).Forget("SnippetsPage.Send", AppLog.Logger);
        }
    }
}
