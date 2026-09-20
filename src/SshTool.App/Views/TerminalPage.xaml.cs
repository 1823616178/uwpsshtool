using System;
using SshTool.App.Controls;
using SshTool.App.Infrastructure;
using SshTool.App.Platform;
using SshTool.App.Terminal;
using SshTool.App.ViewModels;
using SshTool.Core.Appearance;
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
        // P02：省电模式 Banner 数据源（01-DESIGN.md §10；ApiInformation 守卫在内）。
        private readonly Platform.EnergySaverWatcher _energySaver = new Platform.EnergySaverWatcher();

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
            // U13：键条 snippets 键打开片段选择器（发送经 ISshSession.Write）。
            Keys.Action += OnKeyBarAction;
            Term.SnippetRequested += (s, e) => OpenSnippetPicker(Term);
            // U12：物理键盘快捷键的视图局部动作（复制/粘贴/字号）；标签/分屏类动作是
            // 工作区概念，单窗格本页忽略（Continuum 下用 MainPage 宽屏）。
            Term.Shortcut += OnTermShortcut;
            // U09：侧栏与会话 Pivot 共用同一个 SessionsPaneViewModel（MainViewModel 注册）。
            SessionsPaneViewModel paneVm;
            if (ServiceRegistry.TryGet(out paneVm))
            {
                SessionsPane.Attach(paneVm);
            }
        }

        public TerminalViewModel ViewModel { get; private set; }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            StatusBarService.Hide();
            // P01：终端页可见性是屏幕常亮判定的输入之一。
            Platform.KeepAwakeService keepAwake;
            if (ServiceRegistry.TryGet(out keepAwake))
            {
                keepAwake.SetTerminalVisible(true);
            }
            await ViewModel.LoadAsync(e.Parameter as TerminalArgs);
            BindSession();
            ViewModel.AttachNative(native => Term.Session = native);
            ApplyAppearanceForSession();
            // A03：外观变化后已打开终端刷新调色板与字体度量（不重连）。
            if (AppServices.Current != null && AppServices.Current.AppearanceService != null)
            {
                AppServices.Current.AppearanceService.Changed += OnAppearanceChanged;
            }
            // P02：省电模式 Banner（事件在系统线程触发，handler 内封送回 UI）。
            _energySaver.Changed += OnEnergySaverChanged;
            _energySaver.Start();
            UpdateEnergySaverBanner();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            _energySaver.Changed -= OnEnergySaverChanged;
            _energySaver.Stop();
            if (AppServices.Current != null && AppServices.Current.AppearanceService != null)
            {
                AppServices.Current.AppearanceService.Changed -= OnAppearanceChanged;
            }
            StatusBarService.ShowThemed();
            // P01：离开终端页即释放常亮（DisplayRequest 成对，见 KeepAwakeService）。
            Platform.KeepAwakeService keepAwake;
            if (ServiceRegistry.TryGet(out keepAwake))
            {
                keepAwake.SetTerminalVisible(false);
            }
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
                // SessionInfo 的通知虽已由 SessionManager 经 IUiDispatcher 封送，
                // 这里再兜底一次：Title/Dot/Overlay 全是 STA，必须 UI 线程触碰，
                // 否则 Ellipse 等形状在后台线程更新会抛 0x8001010E。
                DispatcherHelper.Post(() =>
                {
                    TitleText.Text = info.Title ?? string.Empty;
                    AddressText.Text = ViewModel.AddressLine;
                    Dot.State = ToDot(info.State);
                    ApplyOverlay(info);
                });
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

        // P02：省电模式 Banner（01-DESIGN.md §10：省电模式下后台连接会被系统断开）。
        // 文案走 Resources.resw（中英）；守卫未通过（IsAvailable == false）则永不显示。
        private void OnEnergySaverChanged(object sender, EventArgs e)
        {
            DispatcherHelper.Post(UpdateEnergySaverBanner);
        }

        private void UpdateEnergySaverBanner()
        {
            bool on = false;
            try
            {
                on = _energySaver.IsEnergySaverOn;
            }
            catch (Exception)
            {
                on = false;
            }
            if (!on)
            {
                EnergySaverBanner.Visibility = Visibility.Collapsed;
                return;
            }
            ResourceLoader loader = ResourceLoader.GetForCurrentView();
            string title = loader.GetString("EnergySaverBanner_Title");
            string message = loader.GetString("EnergySaverBanner_Message");
            if (string.IsNullOrEmpty(title))
            {
                title = "省电模式已开启";
            }
            if (string.IsNullOrEmpty(message))
            {
                message = "省电模式下切换到后台时连接会被系统断开，回到应用后会自动重连";
            }
            EnergySaverBanner.Severity = BannerSeverity.Warning;
            EnergySaverBanner.Title = title;
            EnergySaverBanner.Message = message;
            EnergySaverBanner.Visibility = Visibility.Visible;
        }

        // A03：按会话主机解析有效外观并应用（只动调色板/字体度量，不重连）。
        private void ApplyAppearanceForSession()
        {
            SessionInfo info = ViewModel.Session;
            if (info == null)
            {
                return;
            }
            var ignore = AppearanceApplier.ApplyForHostAsync(Term, info.HostId);
        }

        private void OnAppearanceChanged(object sender, AppearanceChangedEventArgs e)
        {
            SessionInfo info = ViewModel.Session;
            if (info == null || !AppearanceApplier.NeedsRefresh(e, info.HostId))
            {
                return;
            }
            ApplyAppearanceForSession();
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
            flyout.Items.Add(Item("会话", () => SessionsSplit.IsPaneOpen = !SessionsSplit.IsPaneOpen));
            flyout.Items.Add(Item("键条", ToggleKeyBar));
            // U13：终端菜单「片段」打开选择器（锚定菜单按钮）。
            FrameworkElement anchor = sender as FrameworkElement;
            flyout.Items.Add(Item("片段", () => OpenSnippetPicker(anchor ?? Term)));
            // F03：本会话的 SFTP（复用该会话已认证连接挂 SFTP 子系统）。
            flyout.Items.Add(Item("SFTP", OpenSftp));
            flyout.Items.Add(Item("外观", () => Frame.Navigate(typeof(AppearanceListPage))));
            // UI 走查：断开/关闭会话为破坏性项，套红字 DangerMenuItemStyle。
            flyout.Items.Add(DangerItem("断开", () => ViewModel.DisconnectCommand.Execute(null)));
            flyout.Items.Add(DangerItem("关闭会话", () => ViewModel.CloseSessionCommand.Execute(null)));
            flyout.ShowAt((FrameworkElement)sender);
        }

        // F03：带当前会话进入 SFTP 页（SftpArgs.SessionId）。
        private void OpenSftp()
        {
            if (ViewModel.Session != null)
            {
                Frame.Navigate(typeof(SftpPage), new SftpArgs
                {
                    SessionId = ViewModel.Session.SessionId
                });
            }
        }

        // U13：片段选择器统一入口（终端菜单 / 键条 snippets 键 / 右键菜单）。
        private void OpenSnippetPicker(FrameworkElement anchor)
        {
            if (anchor == null)
            {
                anchor = Term;
            }
            SnippetPickerFlyout.Show(anchor, ViewModel.Session);
        }

        private void OnKeyBarAction(object sender, SshTool.Core.Terminal.KeyBarActionEventArgs e)
        {
            if (e == null)
            {
                return;
            }
            switch (e.Action)
            {
                case SshTool.Core.Terminal.KeyBarAction.Snippets:
                    OpenSnippetPicker(Keys);
                    break;
                case SshTool.Core.Terminal.KeyBarAction.Paste:
                    Term.PasteFromClipboard();
                    break;
                case SshTool.Core.Terminal.KeyBarAction.Copy:
                    Term.CopySelectionToClipboard();
                    break;
                case SshTool.Core.Terminal.KeyBarAction.HideKeyboard:
                    Term.ToggleSoftKeyboard();
                    break;
                default:
                    break;
            }
        }

        // U12：宽屏默认隐藏键条后可手动打开/关闭。
        private void ToggleKeyBar()
        {
            Keys.Visibility = Keys.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void OnTermShortcut(object sender, SshTool.Core.Terminal.ShortcutActionEventArgs e)
        {
            if (e == null)
            {
                return;
            }
            switch (e.Action)
            {
                case SshTool.Core.Terminal.ShortcutAction.Copy:
                    Term.CopySelectionToClipboard();
                    break;
                case SshTool.Core.Terminal.ShortcutAction.Paste:
                    Term.PasteFromClipboard();
                    break;
                case SshTool.Core.Terminal.ShortcutAction.FontIncrease:
                    Term.Renderer.FontSize = Term.Renderer.FontSize + 1;
                    break;
                case SshTool.Core.Terminal.ShortcutAction.FontDecrease:
                    Term.Renderer.FontSize = Term.Renderer.FontSize - 1;
                    break;
                case SshTool.Core.Terminal.ShortcutAction.FontReset:
                    try
                    {
                        Term.Renderer.FontSize = (float)SshTool.Core.Models.Defaults.DefaultAppearance().FontSize;
                    }
                    catch (Exception)
                    {
                    }
                    break;
                default:
                    break;
            }
        }

        private static MenuFlyoutItem Item(string text, System.Action action)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (s, e) => action();
            return item;
        }

        // UI 走查：破坏性菜单项（断开/关闭会话）红字样式，菜单在 C# 构造故代码赋 Style。
        private static MenuFlyoutItem DangerItem(string text, System.Action action)
        {
            var item = Item(text, action);
            item.Style = (Style)Application.Current.Resources["DangerMenuItemStyle"];
            return item;
        }
    }
}
