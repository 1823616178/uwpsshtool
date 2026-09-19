using SshTool.App.Infrastructure;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views
{
    public sealed class PlaceholderArgs
    {
        public PlaceholderArgs(string title, string milestone)
        {
            Title = title;
            Milestone = milestone;
        }

        public string Title { get; private set; }
        public string Milestone { get; private set; }
    }

    public sealed partial class PlaceholderPage : Page
    {
        public PlaceholderPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            var args = e.Parameter as PlaceholderArgs;
            string title = args != null ? args.Title : "页面";
            string milestone = args != null ? args.Milestone : "后续";
            Empty.Title = title;
            Empty.Description = "将在 " + milestone + " 提供";
        }

        private void OnBackClick(object sender, RoutedEventArgs e)
        {
            NavigationService nav;
            if (ServiceRegistry.TryGet(out nav) && nav.CanGoBack)
            {
                nav.GoBack();
                return;
            }
            if (Frame != null && Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }
    }
}
