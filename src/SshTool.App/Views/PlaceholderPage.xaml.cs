using SshTool.App.Infrastructure;
using SshTool.Core.Common;
using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views
{
    // Milestone 仅作内部规划口径（日志/注释），一律不上屏。
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
        private static readonly ResourceLoader Loader = ResourceLoader.GetForCurrentView();

        public PlaceholderPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            var args = e.Parameter as PlaceholderArgs;
            string title = args != null && !string.IsNullOrEmpty(args.Title)
                ? args.Title : Loader.GetString("Placeholder_Title");
            string milestone = args != null ? args.Milestone : "later";
            Empty.Title = title;
            Empty.Description = Loader.GetString("Placeholder_ComingSoon/Description");
            // 里程碑代号只进日志，供开发排查；页面不显示 M3/M5/M7 这类内部代号。
            ILogger log;
            if (ServiceRegistry.TryGet(out log))
            {
                log.Log(LogLevel.Info, "Placeholder", title + " 未实现，计划里程碑 " + milestone);
            }
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
