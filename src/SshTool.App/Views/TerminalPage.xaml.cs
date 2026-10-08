using System;
using System.ComponentModel;
using System.Threading.Tasks;
using SshTool.App.Controls;
using SshTool.App.Infrastructure;
using SshTool.App.Platform;
using SshTool.App.Terminal;
using SshTool.App.ViewModels;
using SshTool.Core.Appearance;
using SshTool.Core.Common;
using SshTool.Core.Models;
using SshTool.Core.Sessions;
using Windows.ApplicationModel.Resources;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views
{
    public sealed partial class TerminalPage : Page, IBackHandler
    {
        // P02：省电模式 Banner 数据源（01-DESIGN.md §10；ApiInformation 守卫在内）。
        private readonly Platform.EnergySaverWatcher _energySaver = new Platform.EnergySaverWatcher();
        // R01 (C-02)：导航世代。OnNavigatedTo Begin、OnNavigatedFrom End（取消 + 反序拆除）。
        private readonly NavigationLifetime _lifetime = new NavigationLifetime();
        // R01 (C-01)：BindSession 订阅的 SessionInfo（生命周期长于页面），离开时解除。
        private SessionInfo _boundSession;
        // R01 (C-02)：当前世代号缓存（OnNavigatedTo 写入），供事件路径上的异步守护使用。
        private int _generation;
        // 真机修复（2026-09-29）：软键盘弹出时把键条抬到 SIP 上沿的位移（Y 为负）。
        private readonly TranslateTransform _keyBarLift = new TranslateTransform();
        // G07：信息条展开态已连接时长计时器（仅展开且 Connected 时激活，1 秒间隔）。
        private readonly DispatcherTimer _durationTimer = new DispatcherTimer();

        public TerminalPage()
        {
            ViewModel = new TerminalViewModel();
            this.InitializeComponent();
            _durationTimer.Interval = TimeSpan.FromSeconds(1);
            _durationTimer.Tick += (s, e) => UpdateAddressAndDuration();
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
            // 真机修复（2026-09-28）：键条必须与终端共用同一份粘滞修饰与终端模式——否则点键条 Ctrl
            // 只改键条自己的状态，软键盘打的下一个字母不带 Ctrl（Ctrl+C 永远发不出去），方向键也
            // 不跟随 DECCKM（vim 里方向键乱码）。调试页一直这样接，生产终端页漏了。
            Keys.Sticky = Term.StickyModifiers;
            Keys.Modes = Term.TerminalModes;
            Keys.Input += (s, e) =>
            {
                if (Term.Session != null && e != null && e.Data != null)
                {
                    Term.Session.Write(e.Data);
                }
            };
            // 真机反馈（2026-09-29）：「打开键盘时点 Ctrl/Esc/Tab 不要关闭键盘」。
            // 预防在 KeyBar 侧（ScrollViewer 不再抢焦点）；这里是兜底修复——万一焦点还是
            // 被抢走，趁 InputPane 尚未收完把焦点还给哨兵。只在 SIP 本来弹着时做，
            // 否则点一下键条就会把收起的软键盘硬拉出来。
            Keys.Interacted += OnKeyBarInteracted;
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
            // 真机修复（2026-09-29，「弹出键盘时键条应该在键盘上方而不是消失」）：
            // 键条在页面底行，SIP 一弹出就整条被压在软键盘背后。这里订的是本页自己的子控件
            // （同生同死，无需解除），键条高度变化（布局重建）也要重算一次抬升量。
            Keys.RenderTransform = _keyBarLift;
            Term.InputPaneOcclusionChanged += OnInputPaneOcclusionChanged;
            Keys.SizeChanged += OnKeyBarSizeChanged;
        }

        public TerminalViewModel ViewModel { get; private set; }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            int generation = _lifetime.Begin();
            _generation = generation;
            StatusBarService.Hide();
            // P01：终端页可见性是屏幕常亮判定的输入之一。
            Platform.KeepAwakeService keepAwake;
            if (ServiceRegistry.TryGet(out keepAwake))
            {
                keepAwake.SetTerminalVisible(true);
            }
            // 键条的布局、显隐、触感来自设置（此前设置页改了也不生效）；
            // 02-UI-DESIGN.md §3：鼠标模式/Continuum 键条默认隐藏，可从菜单手动打开。
            SshTool.Core.Storage.SettingsRepository settings = AppServices.Current != null ? AppServices.Current.Settings : null;
            if (settings != null)
            {
                Keys.Layout = settings.KeyBarLayout;
                Keys.HapticsEnabled = settings.HapticsEnabled;
            }
            bool keyBarWanted = settings == null || settings.KeyBarVisible;
            Keys.Visibility = keyBarWanted && !InteractionModeHelper.IsMouseMode
                ? Visibility.Visible
                : Visibility.Collapsed;
            UpdateKeyBarLift();
            // R01 (C-02) + R03 (C-05)：加载/绑定共享世代；fire-and-forget 经 Forget 统一观察。
            LoadAndBindAsync(generation, e.Parameter as TerminalArgs).Forget("TerminalPage.LoadAndBind", AppLog.Logger);
        }

        // R01 (C-02)：加载/绑定共享同一世代——用户在加载期间返回时，await 之后的
        // 订阅与 XAML 触碰全部跳过（IsCurrent 失效即返回）。
        private async Task LoadAndBindAsync(int generation, TerminalArgs args)
        {
            try
            {
                await ViewModel.LoadAsync(args).ConfigureAwait(true);
                if (!_lifetime.IsCurrent(generation))
                {
                    return;
                }
                BindSession();
                ViewModel.AttachNative(native => Term.Session = native);
                await ApplyHostInputOptionsAsync().ConfigureAwait(true);
                if (!_lifetime.IsCurrent(generation))
                {
                    return;
                }
                _lifetime.Track(ViewModel.Detach);
                ApplyAppearanceForSession();
                // A03：外观变化后已打开终端刷新调色板与字体度量（不重连）。
                if (AppServices.Current != null && AppServices.Current.AppearanceService != null)
                {
                    AppServices.Current.AppearanceService.Changed += OnAppearanceChanged;
                    _lifetime.Track(UnsubscribeAppearance);
                }
                // P02：省电模式 Banner（事件在系统线程触发，handler 内封送回 UI）。
                _energySaver.Changed += OnEnergySaverChanged;
                _lifetime.Track(UnsubscribeEnergySaver);
                _energySaver.Start();
                UpdateEnergySaverBanner();
            }
            catch (Exception ex)
            {
                // 加载失败留下半装配状态：End 反序拆掉已登记订阅（幂等，离开时再调安全）。
                // 世代守卫：过期世代的迟到异常（新导航已 Begin）不得拆除当前世代的订阅。
                AppLog.Error("Terminal", "终端页加载失败", ex);
                if (_lifetime.IsCurrent(generation))
                {
                    _lifetime.End();
                }
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            // R01 (C-01/C-02)：End 取消世代（进行中的 LoadAndBindAsync 失效），并按订阅
            // 反序执行 Track 的拆除：省电 → 外观 → ViewModel.Detach → SessionInfo。幂等。
            _lifetime.End();
            CloseFind(false);
            StatusBarService.ShowThemed();
            // P01：离开终端页即释放常亮（DisplayRequest 成对，见 KeepAwakeService）。
            Platform.KeepAwakeService keepAwake;
            if (ServiceRegistry.TryGet(out keepAwake))
            {
                keepAwake.SetTerminalVisible(false);
            }
            Term.Session = null;
            // O03：SessionsPane 绑的是 ServiceRegistry 单例 VM 的 Items，
            // 不断开则单例的集合经 CollectionChanged 攥住本页的 ListView。
            SessionsPane.Detach();
            base.OnNavigatedFrom(e);
        }

        private void UnsubscribeAppearance()
        {
            if (AppServices.Current != null && AppServices.Current.AppearanceService != null)
            {
                AppServices.Current.AppearanceService.Changed -= OnAppearanceChanged;
            }
        }

        private void UnsubscribeEnergySaver()
        {
            _energySaver.Changed -= OnEnergySaverChanged;
            _energySaver.Stop();
        }

        private void BindSession()
        {
            UnbindSession();
            SessionInfo info = ViewModel.Session;
            if (info == null)
            {
                return;
            }
            _boundSession = info;
            TitleText.Text = info.Title ?? string.Empty;
            Dot.State = ToDot(info.State);
            Term.Session = info.NativeSession;
            info.PropertyChanged += OnBoundSessionChanged;
            _lifetime.Track(UnbindSession);
            ApplyOverlay(info);
            RefreshDurationTimer();
        }

        // R01 (C-01)：与 BindSession 成对，幂等（_boundSession 为空即无操作）。
        private void UnbindSession()
        {
            if (_durationTimer.IsEnabled)
            {
                _durationTimer.Stop();
            }
            SessionInfo info = _boundSession;
            if (info == null)
            {
                return;
            }
            _boundSession = null;
            info.PropertyChanged -= OnBoundSessionChanged;
        }

        private void OnBoundSessionChanged(object sender, PropertyChangedEventArgs e)
        {
            // SessionInfo 的通知虽已由 SessionManager 经 IUiDispatcher 封送，
            // 这里再兜底一次：Title/Dot/Overlay 全是 STA，必须 UI 线程触碰，
            // 否则 Ellipse 等形状在后台线程更新会抛 0x8001010E。
            DispatcherHelper.Post(() =>
            {
                // Post 排队期间可能已 Unbind：离开后的旧页面不再触碰自身 XAML。
                SessionInfo info = _boundSession;
                if (info == null)
                {
                    return;
                }
                TitleText.Text = info.Title ?? string.Empty;
                Dot.State = ToDot(info.State);
                ApplyOverlay(info);
                RefreshDurationTimer();
            });
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
                // C-06：重连倒计时文案走 resw 格式化键（双语同增）。
                string format = ResourceLoader.GetForCurrentView().GetString("Overlay_ReconnectingCountdown");
                if (string.IsNullOrEmpty(format))
                {
                    format = "Disconnected. Reconnect attempt {1} in {0} s";
                }
                text = string.Format(format, model.ReconnectInSeconds, model.ReconnectAttempt);
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
                title = "Battery saver is on";
            }
            if (string.IsNullOrEmpty(message))
            {
                message = "With battery saver on, the system drops connections in the background; they reconnect when you return.";
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
            ApplyAppearanceAsync(_generation, info.HostId).Forget("TerminalPage.ApplyAppearance", AppLog.Logger);
        }

        // R01 (C-02)：外观解析是 fire-and-forget，离场后 continuation 不得再触碰 Term。
        // 解析完先查世代；Post 回 UI 线程后再查一次（排队期间可能已离开）——UI 线程上的
        // 这次检查与 OnNavigatedFrom 的 End 串行，是权威判定。
        private async Task ApplyAppearanceAsync(int generation, string hostId)
        {
            AppearanceProfile profile = await AppearanceApplier.ResolveForHostAsync(hostId).ConfigureAwait(false);
            if (profile == null || !_lifetime.IsCurrent(generation))
            {
                return;
            }
            DispatcherHelper.Post(() =>
            {
                if (!_lifetime.IsCurrent(generation))
                {
                    return;
                }
                try
                {
                    Term.ApplyAppearance(profile);
                }
                catch (Exception ex)
                {
                    AppLog.Error("TerminalPage", "应用外观失败", ex);
                }
            });
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

        // V03a（05 §6.2）：点击信息条在 Normal/Expanded 之间切换（单行紧凑 / 双行详情）。
        private void OnInfoTapped(object sender, TappedRoutedEventArgs e)
        {
            ViewModel.ToggleInfoCommand.Execute(null);
            string state = ViewModel.InfoExpanded ? "Expanded" : "Normal";
            VisualStateManager.GoToState(this, state, true);
            RefreshDurationTimer();
        }

        private void RefreshDurationTimer()
        {
            bool shouldRun = ViewModel != null
                && ViewModel.InfoExpanded
                && _boundSession != null
                && _boundSession.State == SessionUiState.Connected
                && _boundSession.ConnectedAt.HasValue;

            if (shouldRun)
            {
                if (!_durationTimer.IsEnabled)
                {
                    _durationTimer.Start();
                }
            }
            else
            {
                if (_durationTimer.IsEnabled)
                {
                    _durationTimer.Stop();
                }
            }
            UpdateAddressAndDuration();
        }

        private void UpdateAddressAndDuration()
        {
            string address = ViewModel != null ? ViewModel.AddressLine ?? string.Empty : string.Empty;
            if (ViewModel != null && ViewModel.InfoExpanded && _boundSession != null && _boundSession.State == SessionUiState.Connected && _boundSession.ConnectedAt.HasValue)
            {
                TimeSpan elapsed = DateTime.UtcNow - _boundSession.ConnectedAt.Value;
                if (elapsed < TimeSpan.Zero)
                {
                    elapsed = TimeSpan.Zero;
                }
                string duration = string.Format("{0:D2}:{1:D2}:{2:D2}", (int)elapsed.TotalHours, elapsed.Minutes, elapsed.Seconds);
                AddressText.Text = string.IsNullOrEmpty(address) ? duration : address + " · " + duration;
            }
            else
            {
                AddressText.Text = address;
            }
        }

        private void OnMenuClick(object sender, RoutedEventArgs e)
        {
            // C-06：终端菜单项文案走 resw 双语。
            ResourceLoader resw = ResourceLoader.GetForCurrentView();
            var flyout = new MenuFlyout();
            flyout.Items.Add(Item(resw.GetString("Terminal_MenuSessions"), () => SessionsSplit.IsPaneOpen = !SessionsSplit.IsPaneOpen));
            flyout.Items.Add(Item(resw.GetString("Terminal_MenuKeyBar"), ToggleKeyBar));
            flyout.Items.Add(Item(Term.ImeMode
                ? Localized.Get("Terminal_MenuImeOff", "切换为英文直通输入")
                : Localized.Get("Terminal_MenuImeOn", "切换为中文输入（输入法）"), ToggleImeMode));
            // U13：终端菜单「片段」打开选择器（锚定菜单按钮）。
            FrameworkElement anchor = sender as FrameworkElement;
            // C-03（§7.5）：菜单关闭后把焦点还给哨兵。仅当开菜单时 SIP 处于弹出态才恢复——
            // 否则会把已收起的软键盘反复拉起；「外观/SFTP/关闭会话」等导航项离场后
            // 世代失效，也不再触碰 Term。「片段」会再开一层 Flyout，此时抢焦点会把
            // 选择器 light-dismiss，故该项抑制恢复，并把此处的采样透传给选择器——
            // 菜单打开期间 SIP 已收起，选择器就地再采样恒为 false。
            int generation = _generation;
            bool restoreSip = Term.IsInputPaneVisible;
            bool suppressRestore = false;
            flyout.Items.Add(Item(resw.GetString("Terminal_MenuSnippets"), () =>
            {
                suppressRestore = true;
                OpenSnippetPicker(anchor ?? Term, restoreSip);
            }));
            // W02：查找会把 SIP 交给查找框，菜单关闭时不再把焦点还给哨兵。
            flyout.Items.Add(Item(resw.GetString("Terminal_MenuFind"), () =>
            {
                suppressRestore = true;
                OpenFind();
            }));
            // F03：本会话的 SFTP（复用该会话已认证连接挂 SFTP 子系统）。
            flyout.Items.Add(Item(resw.GetString("Terminal_MenuSftp"), OpenSftp));
            flyout.Items.Add(Item(resw.GetString("Terminal_MenuAppearance"), () => Frame.Navigate(typeof(AppearanceListPage))));
            // UI 走查：断开/关闭会话为破坏性项，套红字 DangerMenuItemStyle。
            flyout.Items.Add(DangerItem(resw.GetString("Terminal_MenuDisconnect"), () => ViewModel.DisconnectCommand.Execute(null)));
            flyout.Items.Add(DangerItem(resw.GetString("Terminal_MenuCloseSession"), () => ViewModel.CloseSessionCommand.Execute(null)));
            flyout.Closed += (s, args) =>
            {
                if (restoreSip && !suppressRestore && _lifetime.IsCurrent(generation))
                {
                    Term.RestoreInputFocus();
                }
            };
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
        // sipWasVisible：菜单路径由 OnMenuClick 透传开菜单时的采样（菜单打开期间 SIP
        // 已收起，此处就地采样恒为 false）；键条/右键路径不传，就地采样。
        private void OpenSnippetPicker(FrameworkElement anchor, bool? sipWasVisible = null)
        {
            if (anchor == null)
            {
                anchor = Term;
            }
            // C-03（§7.5）：选择器关闭（发送后 DismissRequested 与 light-dismiss 都汇到
            // Flyout.Closed）后恢复哨兵焦点，守卫与终端菜单同形——仅当打开前 SIP 弹着
            // 且世代仍有效才恢复，SIP 原本收起则不做事。采样须在 Show 之前：
            // 选择器一打开焦点就离开哨兵。
            int generation = _generation;
            bool restoreSip = sipWasVisible ?? Term.IsInputPaneVisible;
            var flyout = SnippetPickerFlyout.Show(anchor, ViewModel.Session);
            if (flyout == null)
            {
                return;
            }
            flyout.Closed += (s, args) =>
            {
                if (restoreSip && _lifetime.IsCurrent(generation))
                {
                    Term.RestoreInputFocus();
                }
            };
        }

        private void OnKeyBarInteracted(object sender, EventArgs e)
        {
            if (Term.IsInputPaneVisible)
            {
                // RestoreInputFocus 自带「已有焦点就不动」「物理键盘在场不抢」两道守卫。
                Term.RestoreInputFocus();
            }
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

        // 主机级输入选项：退格发 ^H（HostEdit「退格发送 Ctrl+H」此前保存了却从未应用到终端）。
        private async Task ApplyHostInputOptionsAsync()
        {
            SessionInfo session = ViewModel.Session;
            AppServices services = AppServices.Current;
            if (session == null || services == null || string.IsNullOrEmpty(session.HostId))
            {
                return;
            }
            SshTool.Core.Models.Host host = await services.Hosts.GetByIdAsync(session.HostId).ConfigureAwait(true);
            bool bs = host != null && host.BackspaceSendsCtrlH;
            Term.BackspaceAsBs = bs;
            Keys.BackspaceAsBs = bs;
        }

        // 真机修复：软键盘默认拉丁直通；需要中文时从菜单切 IME 模式（全局偏好，新终端沿用）。
        private void ToggleImeMode()
        {
            bool next = !Term.ImeMode;
            TerminalView.PreferImeMode = next;
            Term.ImeMode = next;
        }

        // U12：宽屏默认隐藏键条后可手动打开/关闭。
        private void ToggleKeyBar()
        {
            Keys.Visibility = Keys.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
            UpdateKeyBarLift();
        }

        private void OnInputPaneOcclusionChanged(object sender, EventArgs e)
        {
            UpdateKeyBarLift();
        }

        private void OnKeyBarSizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateKeyBarLift();
        }

        // 真机修复（2026-09-29）：把键条抬到软键盘上沿，并告诉终端「键条现在是覆盖式的」——
        // 终端已按遮挡高度缩了画布，抬上来的键条会压住它最底下那条，故行数再扣一个键条高度
        // （GridSizeCalculator 的 keyBarOverlays/keyBarHeight）。SIP 收起后两者一起归零。
        // 遮挡矩形随输入法而变（§7.5），只能每次事件重新读，不能写死高度。
        private void UpdateKeyBarLift()
        {
            double lift = 0;
            Rect occluded = Term.InputPaneOccludedRect;
            if (Keys.Visibility == Visibility.Visible && occluded.Height > 0)
            {
                lift = occluded.Height;
                try
                {
                    UIElement root = Window.Current != null ? Window.Current.Content : null;
                    if (root != null)
                    {
                        GeneralTransform transform = Keys.TransformToVisual(root);
                        Point origin = transform.TransformPoint(new Point(0, 0));
                        // origin 已含当前抬升量（_keyBarLift.Y 为负），减掉它才是未抬升时的底边。
                        double bottom = origin.Y - _keyBarLift.Y + Keys.ActualHeight;
                        lift = bottom - occluded.Y;
                    }
                }
                catch (Exception)
                {
                    // TransformToVisual 在未上树/离场时会抛；按整段遮挡高度抬，宁可多抬不要被盖住。
                }
                if (lift < 0)
                {
                    lift = 0;
                }
                if (lift > occluded.Height)
                {
                    lift = occluded.Height;
                }
            }
            _keyBarLift.Y = -lift;
            Term.KeyBarOverlays = lift > 0;
            Term.KeyBarHeight = lift > 0 ? Keys.ActualHeight : 0;
            // 真机诊断（Debug 级，设置→关于→日志级别=调试 才落盘）：抬升量是否算对。
            AppLog.Diag("KeyBar", "lift=" + (int)lift + " occluded=" + (int)occluded.Height
                + " barh=" + (int)Keys.ActualHeight
                + " vis=" + (Keys.Visibility == Visibility.Visible ? 1 : 0));
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
                    catch (Exception ex)
                    {
                        AppLog.Error("TerminalPage", "重置字号失败", ex);
                    }
                    break;
                default:
                    break;
            }
        }

        // ---------- W02 回滚查找（01-DESIGN §16.2） ----------

        private DispatcherTimer _findTimer;

        private void OpenFind()
        {
            InfoBar.Visibility = Visibility.Collapsed;
            FindBar.Visibility = Visibility.Visible;
            FindCount.Text = string.Empty;
            NavigationService nav;
            if (ServiceRegistry.TryGet(out nav))
            {
                nav.RegisterBackHandler(this);
            }
            FindBox.Focus(FocusState.Programmatic);
            FindBox.SelectAll();
        }

        private void CloseFind(bool restoreFocus)
        {
            if (_findTimer != null)
            {
                _findTimer.Stop();
            }
            NavigationService nav;
            if (ServiceRegistry.TryGet(out nav))
            {
                nav.UnregisterBackHandler(this);
            }
            if (FindBar.Visibility != Visibility.Visible)
            {
                return;
            }
            Term.ClearFind();
            FindBar.Visibility = Visibility.Collapsed;
            InfoBar.Visibility = Visibility.Visible;
            if (restoreFocus)
            {
                Term.RestoreInputFocus();
            }
        }

        public bool HandleBack()
        {
            if (FindBar.Visibility != Visibility.Visible)
            {
                return false;
            }
            CloseFind(true);
            return true;
        }

        // 250 ms 去抖：全文读取在 5 万行回滚上不便宜，不在每次击键都跑。
        private void OnFindTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_findTimer == null)
            {
                _findTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _findTimer.Tick += OnFindTimerTick;
            }
            _findTimer.Stop();
            _findTimer.Start();
        }

        private void OnFindTimerTick(object sender, object e)
        {
            _findTimer.Stop();
            if (FindBar.Visibility != Visibility.Visible)
            {
                return;
            }
            int count = Term.FindAll(FindBox.Text);
            if (count > 0)
            {
                // 从最新（最靠下）的命中开始，与桌面终端的反向查找习惯一致。
                Term.ShowMatch(count - 1);
            }
            else
            {
                Term.ClearFind();
            }
            UpdateFindCount();
        }

        private void OnFindKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                Step(-1);
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                CloseFind(true);
                e.Handled = true;
            }
        }

        private void OnFindPrevClick(object sender, RoutedEventArgs e)
        {
            Step(-1);
        }

        private void OnFindNextClick(object sender, RoutedEventArgs e)
        {
            Step(1);
        }

        private void OnFindCloseClick(object sender, RoutedEventArgs e)
        {
            CloseFind(true);
        }

        // -1 = 更旧（向上），+1 = 更新（向下）。
        private void Step(int direction)
        {
            if (Term.MatchCount == 0)
            {
                return;
            }
            Term.ShowMatch(Term.MatchIndex + direction);
            UpdateFindCount();
        }

        private void UpdateFindCount()
        {
            int count = Term.MatchCount;
            if (FindBox.Text.Trim().Length == 0)
            {
                FindCount.Text = string.Empty;
            }
            else if (count == 0)
            {
                FindCount.Text = Localized.Get("Terminal_FindNone", "无结果");
            }
            else
            {
                FindCount.Text = Localized.Format("Terminal_FindCount", "{0}/{1}",
                    Term.MatchIndex + 1, count);
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
