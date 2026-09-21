using SshTool.App.Infrastructure;
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
            BottomBar.PrimaryText = _loader.GetString("Snippets_New.Label");
        }

        public SnippetsViewModel ViewModel { get; private set; }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            var args = e.Parameter as SnippetsArgs;
            ViewModel.AttachSession(args == null ? null : args.SessionId);
            var ignore = ViewModel.RefreshAsync();
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
            Empty.Visibility = ViewModel.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
            NoMatches.Visibility = ViewModel.HasNoMatches ? Visibility.Visible : Visibility.Collapsed;
            SnippetList.Visibility =
                (ViewModel.IsEmpty || ViewModel.HasNoMatches) ? Visibility.Collapsed : Visibility.Visible;
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
            var ignore = ViewModel.SendAsync(row);
        }
    }
}
