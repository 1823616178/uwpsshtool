using System;
using SshTool.App.Dialogs;
using SshTool.App.Infrastructure;
using SshTool.App.ViewModels;
using SshTool.Core.Common;
using SshTool.Core.Hosts;
using SshTool.Core.Models;
using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views
{
    public sealed class HostEditArgs
    {
        public HostEditMode Mode { get; private set; }
        public string HostId { get; private set; }

        public static HostEditArgs New()
        {
            return new HostEditArgs { Mode = HostEditMode.New };
        }

        public static HostEditArgs Edit(string hostId)
        {
            return new HostEditArgs { Mode = HostEditMode.Edit, HostId = hostId };
        }

        public static HostEditArgs Duplicate(string hostId)
        {
            return new HostEditArgs { Mode = HostEditMode.Duplicate, HostId = hostId };
        }
    }

    public sealed partial class HostEditPage : Page, IBackHandler
    {
        private readonly ResourceLoader _loader = ResourceLoader.GetForCurrentView();
        // R01 (C-02)：导航世代，离开后加载链不再触碰 UI。
        private readonly NavigationLifetime _lifetime = new NavigationLifetime();
        private bool _abandonConfirmed;
        private bool _suppressCombo;
        private bool _suppressAuth;

        public HostEditPage()
        {
            ViewModel = new HostEditViewModel(AppServices.Current);
            this.InitializeComponent();
        }

        public HostEditViewModel ViewModel { get; private set; }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            int generation = _lifetime.Begin();
            NavigationService nav;
            if (ServiceRegistry.TryGet(out nav))
            {
                nav.RegisterBackHandler(this);
            }
            LoadAsync(generation, e).Forget("HostEditPage.Load", AppLog.Logger);
            SetupBottomBar();
        }

        // V04b（§6.4）：装配底部固定操作条——主操作=保存，溢出=测试连接；高级折叠 chevron 复位。
        private void SetupBottomBar()
        {
            BottomBar.PrimaryText = _loader.GetString("HostEdit_Save");
            var flyout = new MenuFlyout();
            var testItem = new MenuFlyoutItem { Text = _loader.GetString("HostEdit_TestConnect") };
            testItem.Click += (s, args) => RunTestAsync().Forget("HostEditPage.RunTest", AppLog.Logger);
            flyout.Items.Add(testItem);
            BottomBar.OverflowFlyout = flyout;
            AutomationProperties.SetName(AdvancedToggle, _loader.GetString("HostEdit_AdvancedToggle_Chevron"));
        }

        // R01 (C-02)：每个 await 后先查世代，页面已离开则不再触碰 ComboBox 等 XAML。
        private async System.Threading.Tasks.Task LoadAsync(int generation, NavigationEventArgs e)
        {
            try
            {
                if (e.NavigationMode == NavigationMode.New)
                {
                    await ViewModel.LoadAsync(e.Parameter as HostEditArgs);
                    if (!_lifetime.IsCurrent(generation))
                    {
                        return;
                    }
                    BindLoaded();
                }
                else
                {
                    await ViewModel.ReloadGroupsAsync();
                    if (!_lifetime.IsCurrent(generation))
                    {
                        return;
                    }
                    _suppressCombo = true;
                    GroupBox.ItemsSource = ViewModel.Groups;
                    SelectById(GroupBox, ViewModel.GroupId);
                    _suppressCombo = false;
                }
                // A03：从外观管理页返回时刷新下拉（保留当前选择）。
                await ViewModel.ReloadAppearancesAsync();
                if (!_lifetime.IsCurrent(generation))
                {
                    return;
                }
                _suppressCombo = true;
                AppearanceBox.ItemsSource = ViewModel.Appearances;
                SelectById(AppearanceBox, ViewModel.AppearanceId);
                _suppressCombo = false;
            }
            catch (Exception ex)
            {
                AppLog.Error("HostEdit", "host edit load failed", ex);
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            _lifetime.End();
            NavigationService nav;
            if (ServiceRegistry.TryGet(out nav))
            {
                nav.UnregisterBackHandler(this);
            }
            base.OnNavigatedFrom(e);
        }

        public bool HandleBack()
        {
            if (_abandonConfirmed || !ViewModel.IsDirty)
            {
                return false;
            }
            ConfirmAbandonAsync().Forget("HostEditPage.ConfirmAbandon", AppLog.Logger);
            return true;
        }

        private void BindLoaded()
        {
            TitleText.Text = ViewModel.Title;
            _suppressCombo = true;
            NameBox.Text = ViewModel.Name;
            HostBox.Text = ViewModel.HostName;
            PortBox.Text = ViewModel.PortText;
            UserBox.Text = ViewModel.Username;
            KeepaliveBox.Text = ViewModel.KeepaliveText;
            FingerprintText.Text = string.IsNullOrEmpty(ViewModel.HostFingerprint)
                ? _loader.GetString("HostEdit_FingerprintNone") : ViewModel.HostFingerprint;
            InitBox.Text = ViewModel.InitCommandsText ?? string.Empty;
            TmuxNameBox.Text = ViewModel.TmuxSessionName ?? string.Empty;
            BackspaceSwitch.IsOn = ViewModel.BackspaceSendsCtrlH;
            TmuxSwitch.IsOn = ViewModel.TmuxAutoAttach;
            GroupBox.ItemsSource = ViewModel.Groups;
            JumpBox.ItemsSource = ViewModel.Jumps;
            AppearanceBox.ItemsSource = ViewModel.Appearances;
            TermTypeBox.ItemsSource = ViewModel.TermTypes;
            EnvList.ItemsSource = ViewModel.EnvVars;
            TunnelList.ItemsSource = ViewModel.Tunnels;
            KeyBox.ItemsSource = ViewModel.Keys;
            PasswordBox.PlaceholderText = ViewModel.PasswordPlaceholder;
            PassphraseBox.PlaceholderText = ViewModel.PassphrasePlaceholder;
            _suppressAuth = true;
            AuthPasswordRadio.IsChecked = ViewModel.Credentials.AuthType == AuthType.Password;
            AuthKeyRadio.IsChecked = ViewModel.Credentials.AuthType == AuthType.Key;
            AuthAgentRadio.IsChecked = ViewModel.Credentials.AuthType == AuthType.Agent;
            RememberPasswordSwitch.IsOn = ViewModel.Credentials.RememberPassword;
            RememberPassphraseSwitch.IsOn = ViewModel.Credentials.RememberPassphrase;
            SelectById(KeyBox, ViewModel.Credentials.KeyId);
            ApplyAuthPanels(ViewModel.Credentials.AuthType);
            _suppressAuth = false;
            SelectById(GroupBox, ViewModel.GroupId);
            SelectById(JumpBox, ViewModel.JumpHostId);
            SelectById(AppearanceBox, ViewModel.AppearanceId);
            SelectById(TermTypeBox, ViewModel.TermType);
            _suppressCombo = false;
            ShowErrors();
        }

        private async System.Threading.Tasks.Task ConfirmAbandonAsync()
        {
            ConfirmDialogResult result = await ConfirmDialog.ShowAsync(
                _loader.GetString("HostEdit_AbandonTitle"),
                _loader.GetString("HostEdit_AbandonMessage"),
                _loader.GetString("HostEdit_AbandonConfirm"),
                _loader.GetString("HostEdit_AbandonCancel"),
                isDanger: true);
            if (result.Confirmed)
            {
                _abandonConfirmed = true;
                NavigationService nav;
                if (ServiceRegistry.TryGet(out nav))
                {
                    nav.GoBack();
                }
            }
        }

        private async System.Threading.Tasks.Task RunTestAsync()
        {
            TestOverlay.Message = _loader.GetString("HostEdit_TestMessage");
            TestOverlay.IsActive = true;
            try
            {
                SshTool.Core.Sessions.TestConnectResult result = await ViewModel.TestAsync();
                string text = _loader.GetString(result.MessageKey ?? "Error_500");
                TestOverlay.IsActive = false;
                await ConfirmDialog.ShowAsync(result.Success
                        ? _loader.GetString("HostEdit_TestTitle")
                        : _loader.GetString("HostEdit_TestFailedTitle"),
                    string.IsNullOrEmpty(text) ? result.MessageKey : text,
                    _loader.GetString("HostEdit_DialogOk"),
                    _loader.GetString("HostEdit_DialogClose"));
            }
            catch (Exception ex)
            {
                AppLog.Error("HostEdit", "test connection failed", ex);
                TestOverlay.IsActive = false;
            }
            finally
            {
                TestOverlay.IsActive = false;
            }
        }

        // V04b：OverflowClick 触发器复用同一测试流程。
        private async void OnTestClick(object sender, EventArgs e)
        {
            try
            {
                await RunTestAsync();
            }
            catch (Exception ex)
            {
                AppLog.Error("HostEditPage", "OnTestClick failed", ex);
            }
        }

        private async void OnSaveClick(object sender, EventArgs e)
        {
            try
            {
                await ViewModel.SaveCoreAsync();
                if (ViewModel.LastSaveHadErrors)
                {
                    ShowErrors();
                    FocusField(ViewModel.FirstErrorField());
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("HostEditPage", "OnSaveClick failed", ex);
            }
        }

        // V04b：高级 Section 折叠/展开。点击头部翻转 chevron 并切换内容可见性。
        private void OnAdvancedToggleClick(object sender, RoutedEventArgs e)
        {
            bool collapsed = AdvancedContent.Visibility == Visibility.Collapsed;
            AdvancedContent.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
            AdvancedChevron.Glyph = (string)Application.Current.Resources
                [collapsed ? "IconChevronDown" : "IconChevronRight"];
        }

        private void OnNameChanged(object sender, TextChangedEventArgs e)
        {
            ViewModel.Name = NameBox.Text;
            ShowErrors();
        }

        private void OnHostChanged(object sender, TextChangedEventArgs e)
        {
            ViewModel.HostName = HostBox.Text;
            ShowErrors();
        }

        private void OnPortChanged(object sender, TextChangedEventArgs e)
        {
            ViewModel.PortText = PortBox.Text;
            ShowErrors();
        }

        private void OnUserChanged(object sender, TextChangedEventArgs e)
        {
            ViewModel.Username = UserBox.Text;
            ShowErrors();
        }

        private void OnKeepaliveChanged(object sender, TextChangedEventArgs e)
        {
            ViewModel.KeepaliveText = KeepaliveBox.Text;
            ShowErrors();
        }

        private void OnInitChanged(object sender, TextChangedEventArgs e)
        {
            ViewModel.InitCommandsText = InitBox.Text;
        }

        private void OnTmuxNameChanged(object sender, TextChangedEventArgs e)
        {
            ViewModel.TmuxSessionName = TmuxNameBox.Text;
        }

        private void OnManageGroups(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(GroupManagePage));
        }

        // A03：外观下拉接入外观列表页。
        private void OnManageAppearances(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(AppearanceListPage));
        }

        private void OnGroupChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressCombo)
            {
                return;
            }
            ViewModel.GroupId = SelectedId(GroupBox);
            ShowErrors();
        }

        private void OnJumpChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressCombo)
            {
                return;
            }
            ViewModel.JumpHostId = SelectedId(JumpBox);
            ShowErrors();
        }

        private void OnAppearanceChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressCombo)
            {
                return;
            }
            ViewModel.AppearanceId = SelectedId(AppearanceBox);
        }

        private void OnTermTypeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressCombo)
            {
                return;
            }
            ViewModel.TermType = SelectedId(TermTypeBox);
            ShowErrors();
        }

        private void OnBackspaceToggled(object sender, RoutedEventArgs e)
        {
            ViewModel.BackspaceSendsCtrlH = BackspaceSwitch.IsOn;
        }

        private void OnTmuxToggled(object sender, RoutedEventArgs e)
        {
            ViewModel.TmuxAutoAttach = TmuxSwitch.IsOn;
        }

        private void OnClearFingerprint(object sender, RoutedEventArgs e)
        {
            ViewModel.ClearFingerprintCommand.Execute(null);
            FingerprintText.Text = _loader.GetString("HostEdit_FingerprintNone");
        }

        private void OnAddEnv(object sender, RoutedEventArgs e)
        {
            ViewModel.AddEnvCommand.Execute(null);
        }

        private void OnRemoveEnv(object sender, RoutedEventArgs e)
        {
            var item = ((FrameworkElement)sender).DataContext as EnvVarItem;
            ViewModel.RemoveEnv(item);
        }

        private void OnAddTunnel(object sender, RoutedEventArgs e)
        {
            ViewModel.AddTunnelCommand.Execute(null);
        }

        private void OnTunnelItemClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as IdNameOption;
            if (item != null && !string.IsNullOrEmpty(item.Id))
            {
                Frame.Navigate(typeof(TunnelEditPage), TunnelEditArgs.Edit(item.Id));
            }
        }

        private async void OnAuthPasswordChecked(object sender, RoutedEventArgs e)
        {
            try
            {
                await SwitchAuthAsync(AuthType.Password);
            }
            catch (Exception ex)
            {
                AppLog.Error("HostEditPage", "OnAuthPasswordChecked failed", ex);
            }
        }

        private async void OnAuthKeyChecked(object sender, RoutedEventArgs e)
        {
            try
            {
                await SwitchAuthAsync(AuthType.Key);
            }
            catch (Exception ex)
            {
                AppLog.Error("HostEditPage", "OnAuthKeyChecked failed", ex);
            }
        }

        private async void OnAuthAgentChecked(object sender, RoutedEventArgs e)
        {
            try
            {
                await SwitchAuthAsync(AuthType.Agent);
            }
            catch (Exception ex)
            {
                AppLog.Error("HostEditPage", "OnAuthAgentChecked failed", ex);
            }
        }

        private async System.Threading.Tasks.Task SwitchAuthAsync(AuthType next)
        {
            if (_suppressAuth || ViewModel.Credentials == null)
            {
                return;
            }
            AuthSwitchPreview preview = ViewModel.Credentials.PreviewSwitch(next);
            if (preview.NeedsConfirm)
            {
                ConfirmDialogResult confirm = await ConfirmDialog.ShowAsync(
                    _loader.GetString("HostEdit_SwitchAuthTitle"),
                    _loader.GetString(preview.Drop == AuthSwitchDrop.Password
                        ? "HostEdit_SwitchAuthDropPassword"
                        : "HostEdit_SwitchAuthDropKey"),
                    _loader.GetString("HostEdit_SwitchAuthConfirm"),
                    _loader.GetString("HostEdit_SwitchAuthCancel"),
                    isDanger: true);
                if (!confirm.Confirmed)
                {
                    _suppressAuth = true;
                    AuthPasswordRadio.IsChecked = ViewModel.Credentials.AuthType == AuthType.Password;
                    AuthKeyRadio.IsChecked = ViewModel.Credentials.AuthType == AuthType.Key;
                    AuthAgentRadio.IsChecked = ViewModel.Credentials.AuthType == AuthType.Agent;
                    _suppressAuth = false;
                    return;
                }
            }
            ViewModel.Credentials.ApplySwitch(next);
            ApplyAuthPanels(next);
            ViewModel.RaiseCredentialStates();
        }

        private void ApplyAuthPanels(AuthType type)
        {
            PasswordPanel.Visibility = type == AuthType.Password ? Visibility.Visible : Visibility.Collapsed;
            KeyPanel.Visibility = type == AuthType.Key ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnPasswordChanged(object sender, RoutedEventArgs e)
        {
            if (ViewModel.Credentials != null)
            {
                ViewModel.Credentials.SetPassword(PasswordBox.Password);
                ViewModel.RaiseCredentialStates();
            }
        }

        private void OnPassphraseChanged(object sender, RoutedEventArgs e)
        {
            if (ViewModel.Credentials != null)
            {
                ViewModel.Credentials.SetPassphrase(PassphraseBox.Password);
                ViewModel.RaiseCredentialStates();
            }
        }

        private void OnRememberPasswordToggled(object sender, RoutedEventArgs e)
        {
            if (_suppressAuth || ViewModel.Credentials == null)
            {
                return;
            }
            ViewModel.Credentials.SetRememberPassword(RememberPasswordSwitch.IsOn);
            ViewModel.RaiseCredentialStates();
        }

        private void OnRememberPassphraseToggled(object sender, RoutedEventArgs e)
        {
            if (_suppressAuth || ViewModel.Credentials == null)
            {
                return;
            }
            ViewModel.Credentials.SetRememberPassphrase(RememberPassphraseSwitch.IsOn);
            ViewModel.RaiseCredentialStates();
        }

        private void OnKeyChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressCombo || ViewModel.Credentials == null)
            {
                return;
            }
            ViewModel.Credentials.KeyId = SelectedId(KeyBox);
            ViewModel.RaiseCredentialStates();
        }

        private async void OnImportKey(object sender, RoutedEventArgs e)
        {
            try
            {
                // K02：导入对话框；新建或去重命中都选中该密钥。
                KeyImportDialogResult result = await KeyImportDialog.ShowAsync();
                if (result == null || result.Cancelled)
                {
                    return;
                }
                string selectId = result.Created == null ? result.ExistingKeyId : result.Created.Id;
                if (string.IsNullOrEmpty(selectId))
                {
                    return;
                }
                await ViewModel.ReloadKeysAsync();
                _suppressCombo = true;
                KeyBox.ItemsSource = ViewModel.Keys;
                SelectById(KeyBox, selectId);
                _suppressCombo = false;
                if (ViewModel.Credentials != null)
                {
                    ViewModel.Credentials.KeyId = selectId;
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("HostEditPage", "OnImportKey failed", ex);
            }
        }

        private async void OnGenerateKey(object sender, RoutedEventArgs e)
        {
            try
            {
                // K02：生成对话框；成功后选中新密钥。
                KeyGenerateDialogResult result = await KeyGenerateDialog.ShowAsync();
                if (result == null || result.Cancelled || result.Created == null)
                {
                    return;
                }
                await ViewModel.ReloadKeysAsync();
                _suppressCombo = true;
                KeyBox.ItemsSource = ViewModel.Keys;
                SelectById(KeyBox, result.Created.Id);
                _suppressCombo = false;
                if (ViewModel.Credentials != null)
                {
                    ViewModel.Credentials.KeyId = result.Created.Id;
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("HostEditPage", "OnGenerateKey failed", ex);
            }
        }

        private void ShowErrors()
        {
            SetError(NameError, "name");
            SetError(HostError, "host");
            SetError(PortError, "port");
            SetError(UserError, "username");
            SetError(KeepaliveError, "keepalive");
            SetError(EnvError, "envVars");
            SetError(JumpError, "jumpHostId");
        }

        private void SetError(TextBlock block, string field)
        {
            string key;
            if (ViewModel.Errors != null && ViewModel.Errors.TryGetValue(field, out key))
            {
                string text = _loader.GetString(key);
                block.Text = string.IsNullOrEmpty(text) ? key : text;
                block.Visibility = Visibility.Visible;
            }
            else
            {
                block.Text = string.Empty;
                block.Visibility = Visibility.Collapsed;
            }
        }

        // V04b：原 FocusField 依赖 Pivot.SelectedIndex 切换标签；现结构已无 Pivot，直接聚焦目标控件。
        private void FocusField(string field)
        {
            if (string.IsNullOrEmpty(field))
            {
                return;
            }
            Control target = null;
            switch (field)
            {
                case "name":
                    target = NameBox;
                    break;
                case "host":
                    target = HostBox;
                    break;
                case "port":
                    target = PortBox;
                    break;
                case "username":
                    target = UserBox;
                    break;
                case "keepalive":
                    target = KeepaliveBox;
                    break;
                case "envVars":
                    target = EnvList;
                    break;
                case "jumpHostId":
                    target = JumpBox;
                    break;
                case "termType":
                    target = TermTypeBox;
                    break;
            }
            if (target != null)
            {
                Control focus = target;
                DispatcherHelper.Post(() => focus.Focus(FocusState.Programmatic));
            }
        }

        private static string SelectedId(ComboBox box)
        {
            var option = box.SelectedItem as IdNameOption;
            return option == null ? string.Empty : option.Id;
        }

        private static void SelectById(ComboBox box, string id)
        {
            string want = id ?? string.Empty;
            for (int i = 0; i < box.Items.Count; i++)
            {
                var option = box.Items[i] as IdNameOption;
                if (option != null && option.Id == want)
                {
                    box.SelectedIndex = i;
                    return;
                }
            }
            if (box.Items.Count > 0)
            {
                box.SelectedIndex = 0;
            }
        }
    }
}
