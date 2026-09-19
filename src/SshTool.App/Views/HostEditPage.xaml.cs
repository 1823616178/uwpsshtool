using System;
using SshTool.App.Dialogs;
using SshTool.App.Infrastructure;
using SshTool.App.ViewModels;
using SshTool.Core.Hosts;
using SshTool.Core.Models;
using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml;
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
        private bool _abandonConfirmed;
        private bool _suppressCombo;
        private bool _suppressAuth;

        public HostEditPage()
        {
            ViewModel = new HostEditViewModel(AppServices.Current);
            this.InitializeComponent();
        }

        public HostEditViewModel ViewModel { get; private set; }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            NavigationService nav;
            if (ServiceRegistry.TryGet(out nav))
            {
                nav.RegisterBackHandler(this);
            }
            if (e.NavigationMode == NavigationMode.New)
            {
                await ViewModel.LoadAsync(e.Parameter as HostEditArgs);
                BindLoaded();
            }
            else
            {
                await ViewModel.ReloadGroupsAsync();
                _suppressCombo = true;
                GroupBox.ItemsSource = ViewModel.Groups;
                SelectById(GroupBox, ViewModel.GroupId);
                _suppressCombo = false;
            }
            // A03：从外观管理页返回时刷新下拉（保留当前选择）。
            await ViewModel.ReloadAppearancesAsync();
            _suppressCombo = true;
            AppearanceBox.ItemsSource = ViewModel.Appearances;
            SelectById(AppearanceBox, ViewModel.AppearanceId);
            _suppressCombo = false;
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
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
            var ignore = ConfirmAbandonAsync();
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
                ? "（无）" : ViewModel.HostFingerprint;
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
                "放弃修改？", "未保存的更改将丢失。", "放弃", "继续编辑");
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

        private async void OnTestClick(object sender, RoutedEventArgs e)
        {
            TestOverlay.Message = "测试连接…";
            TestOverlay.IsActive = true;
            try
            {
                SshTool.Core.Sessions.TestConnectResult result = await ViewModel.TestAsync();
                string text = _loader.GetString(result.MessageKey ?? "Error_500");
                TestOverlay.IsActive = false;
                await ConfirmDialog.ShowAsync(result.Success ? "测试连接" : "测试失败",
                    string.IsNullOrEmpty(text) ? result.MessageKey : text, "确定", "关闭");
            }
            finally
            {
                TestOverlay.IsActive = false;
            }
        }

        private async void OnSaveClick(object sender, RoutedEventArgs e)
        {
            await ViewModel.SaveCoreAsync();
            if (ViewModel.LastSaveHadErrors)
            {
                ShowErrors();
                FocusField(ViewModel.FirstErrorField());
            }
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            if (!HandleBack())
            {
                NavigationService nav;
                if (ServiceRegistry.TryGet(out nav))
                {
                    nav.GoBack();
                }
            }
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
            FingerprintText.Text = "（无）";
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

        private async void OnAuthPasswordChecked(object sender, RoutedEventArgs e)
        {
            await SwitchAuthAsync(AuthType.Password);
        }

        private async void OnAuthKeyChecked(object sender, RoutedEventArgs e)
        {
            await SwitchAuthAsync(AuthType.Key);
        }

        private async void OnAuthAgentChecked(object sender, RoutedEventArgs e)
        {
            await SwitchAuthAsync(AuthType.Agent);
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
                    "切换认证方式？", preview.Message, "切换", "取消");
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
            }
        }

        private void OnPassphraseChanged(object sender, RoutedEventArgs e)
        {
            if (ViewModel.Credentials != null)
            {
                ViewModel.Credentials.SetPassphrase(PassphraseBox.Password);
            }
        }

        private void OnRememberPasswordToggled(object sender, RoutedEventArgs e)
        {
            if (_suppressAuth || ViewModel.Credentials == null)
            {
                return;
            }
            ViewModel.Credentials.SetRememberPassword(RememberPasswordSwitch.IsOn);
        }

        private void OnRememberPassphraseToggled(object sender, RoutedEventArgs e)
        {
            if (_suppressAuth || ViewModel.Credentials == null)
            {
                return;
            }
            ViewModel.Credentials.SetRememberPassphrase(RememberPassphraseSwitch.IsOn);
        }

        private void OnKeyChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressCombo || ViewModel.Credentials == null)
            {
                return;
            }
            ViewModel.Credentials.KeyId = SelectedId(KeyBox);
        }

        private async void OnImportKey(object sender, RoutedEventArgs e)
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

        private async void OnGenerateKey(object sender, RoutedEventArgs e)
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

        private void FocusField(string field)
        {
            if (string.IsNullOrEmpty(field))
            {
                return;
            }
            EditPivot.SelectedIndex = HostEditState.PivotIndexForField(field);
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
