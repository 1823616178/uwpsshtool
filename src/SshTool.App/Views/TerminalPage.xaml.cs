using SshTool.App.Controls;
using SshTool.App.Infrastructure;
using SshTool.App.Platform;
using SshTool.App.ViewModels;
using SshTool.Core.Sessions;
using Windows.ApplicationModel.Resources;
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
            Overlay.Cancel += (s, e) => ViewModel.CloseSessionCommand.Execute(null);
            Overlay.ReconnectNow += (s, e) =>
            {
                if (ViewModel.Session != null && AppServices.Current.Sessions != null)
                {
                    AppServices.Current.Sessions.ReconnectNow(ViewModel.Session.SessionId);
                }
            };
            Overlay.StopReconnect += (s, e) => ViewModel.DisconnectCommand.Execute(null);
            Overlay.Retry += (s, e) =>
            {
                if (ViewModel.Session != null && AppServices.Current.Sessions != null)
                {
                    AppServices.Current.Sessions.ReconnectNow(ViewModel.Session.SessionId);
                }
            };
            Overlay.CloseSession += (s, e) => ViewModel.CloseSessionCommand.Execute(null);
            Overlay.EditHost += (s, e) =>
            {
                if (ViewModel.Session != null)
                {
                    Frame.Navigate(typeof(HostEditPage), HostEditArgs.Edit(ViewModel.Session.HostId));
                }
            };
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
                ApplyOverlay(info);
            };
            ApplyOverlay(info);
        }

        private void ApplyOverlay(SessionInfo info)
        {
            OverlayModel model = OverlayStateDeriver.Derive(info);
            Overlay.Apply(model);
            if (model.Kind == OverlayKind.None)
            {
                return;
            }
            string text = string.Empty;
            if (model.Kind == OverlayKind.Reconnecting)
            {
                text = "连接已断开，" + model.ReconnectInSeconds.ToString() + " 秒后第 "
                    + model.ReconnectAttempt.ToString() + " 次重连";
            }
            else if (!string.IsNullOrEmpty(model.MessageKey))
            {
                text = ResourceLoader.GetForCurrentView().GetString(model.MessageKey);
            }
            Overlay.SetMessage(string.IsNullOrEmpty(text) ? model.MessageKey : text);
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
