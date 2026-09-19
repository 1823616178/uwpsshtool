using System;
using SshTool.App.Dialogs;
using SshTool.App.Infrastructure;
using SshTool.App.ViewModels;
using SshTool.Core.Hosts;
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
            await ViewModel.LoadAsync(e.Parameter as HostEditArgs);
            BindLoaded();
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
