using SshTool.App.Controls;
using SshTool.App.Infrastructure;
using SshTool.App.Platform;
using SshTool.App.ViewModels;
using SshTool.Core.Hosts;
using SshTool.Core.Sessions;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views
{
    public sealed partial class TerminalPage : Page
    {
        public TerminalPage()
        {
            ViewModel = new TerminalViewModel();
            this.InitializeComponent();
            Keys.Input += (s, e) =>
            {
                if (Term.Session != null && e != null && e.Data != null)
                {
                    Term.Session.Write(e.Data);
                }
            };
        }

        public TerminalViewModel ViewModel { get; private set; }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            StatusBarService.Hide();
            await ViewModel.LoadAsync(e.Parameter as TerminalArgs);
            BindSession();
            ViewModel.AttachNative(native => Term.Session = native);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            StatusBarService.ShowThemed();
            Term.Session = null;
            base.OnNavigatedFrom(e);
        }

        private void BindSession()
        {
            SessionInfo info = ViewModel.Session;
            if (info == null)
            {
                return;
            }
            TitleText.Text = info.Title ?? string.Empty;
            AddressText.Text = ViewModel.AddressLine;
            Dot.State = ToDot(info.State);
            Term.Session = info.NativeSession;
            info.PropertyChanged += (s, args) =>
            {
                TitleText.Text = info.Title ?? string.Empty;
                AddressText.Text = ViewModel.AddressLine;
                Dot.State = ToDot(info.State);
            };
        }

        private static StatusDotState ToDot(SessionUiState state)
        {
            switch (state)
            {
                case SessionUiState.Connected:
                    return StatusDotState.Connected;
                case SessionUiState.Reconnecting:
                    return StatusDotState.Reconnecting;
                case SessionUiState.Connecting:
                case SessionUiState.Authenticating:
                    return StatusDotState.Connecting;
                case SessionUiState.Error:
                    return StatusDotState.Error;
                default:
                    return StatusDotState.Disconnected;
            }
        }

        private void OnInfoTapped(object sender, TappedRoutedEventArgs e)
        {
            ViewModel.ToggleInfoCommand.Execute(null);
            InfoBar.Visibility = ViewModel.InfoCollapsed ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnMenuClick(object sender, RoutedEventArgs e)
        {
            var flyout = new MenuFlyout();
            flyout.Items.Add(Item("片段", () => ViewModel.SnippetsCommand.Execute(null)));
            flyout.Items.Add(Item("断开", () => ViewModel.DisconnectCommand.Execute(null)));
            flyout.Items.Add(Item("关闭会话", () => ViewModel.CloseSessionCommand.Execute(null)));
            flyout.ShowAt((FrameworkElement)sender);
        }

        private static MenuFlyoutItem Item(string text, System.Action action)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (s, e) => action();
            return item;
        }
    }
}
