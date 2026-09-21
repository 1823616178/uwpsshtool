using System;
using System.Linq;
using System.Threading.Tasks;
using SshTool.App.Dialogs;
using SshTool.App.Infrastructure;
using SshTool.App.ViewModels;
using SshTool.Core.Common;
using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views
{
    public sealed class TunnelEditArgs
    {
        public string TunnelId { get; set; }
        public string ServerId { get; set; }

        public static TunnelEditArgs New(string serverId = null)
        {
            return new TunnelEditArgs { ServerId = serverId };
        }

        public static TunnelEditArgs Edit(string tunnelId)
        {
            return new TunnelEditArgs { TunnelId = tunnelId };
        }
    }

    public sealed partial class TunnelEditPage : Page, IBackHandler
    {
        private readonly ResourceLoader _loader = ResourceLoader.GetForCurrentView();
        private readonly NavigationLifetime _lifetime = new NavigationLifetime();
        private bool _abandonConfirmed;
        private bool _suppress;

        public TunnelEditPage()
        {
            ViewModel = new TunnelEditViewModel(AppServices.Current);
            this.InitializeComponent();
        }

        public TunnelEditViewModel ViewModel { get; }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            int generation = _lifetime.Begin();
            NavigationService nav;
            if (ServiceRegistry.TryGet(out nav))
            {
                nav.RegisterBackHandler(this);
            }
            var args = e.Parameter as TunnelEditArgs;
            var ignore = LoadAsync(generation, args);
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
            var ignore = ConfirmAbandonAsync();
            return true;
        }

        private async Task ConfirmAbandonAsync()
        {
            string title = _loader.GetString("TunnelEdit_UnsavedChanges_Title") ?? "放弃修改？";
            string msg = _loader.GetString("TunnelEdit_UnsavedChanges_Message") ?? "未保存的修改将丢失。";
            string discard = _loader.GetString("TunnelEdit_Discard") ?? "放弃";
            string cancel = _loader.GetString("TunnelEdit_Cancel") ?? (_loader.GetString("Dialog_Cancel") ?? "取消");

            var result = await ConfirmDialog.ShowAsync(title, msg, discard, cancel, isDanger: true);
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

        private void OnBackRequested(object sender, EventArgs e)
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

        private async Task LoadAsync(int generation, TunnelEditArgs args)
        {
            try
            {
                await ViewModel.LoadAsync(args?.TunnelId, args?.ServerId);
                if (!_lifetime.IsCurrent(generation))
                {
                    return;
                }
                BindLoaded();
            }
            catch (Exception ex)
            {
                AppLog.Error("TunnelEditPage", "LoadAsync failed", ex);
            }
        }

        private void BindLoaded()
        {
            PageHeader.Title = ViewModel.Title;

            _suppress = true;
            NameBox.Text = ViewModel.Name;
            TypeBox.ItemsSource = ViewModel.Types;
            TypeBox.SelectedItem = ViewModel.Types.FirstOrDefault(t => t.Type == ViewModel.Type);

            ServerBox.ItemsSource = ViewModel.Hosts;
            ServerBox.SelectedItem = ViewModel.Hosts.FirstOrDefault(h => h.Id == ViewModel.ServerId);

            GroupBox.ItemsSource = ViewModel.Groups;
            GroupBox.SelectedItem = ViewModel.Groups.FirstOrDefault(g => g.Id == ViewModel.GroupId);

            ListenHostBox.Text = ViewModel.ListenHost;
            ListenPortBox.Text = ViewModel.ListenPortText;
            DestHostBox.Text = ViewModel.DestHost;
            DestPortBox.Text = ViewModel.DestPortText;

            DestServerBox.ItemsSource = ViewModel.Hosts;
            DestServerBox.SelectedItem = ViewModel.Hosts.FirstOrDefault(h => h.Id == ViewModel.DestServerId);

            AutoReconnectSwitch.IsOn = ViewModel.AutoReconnect;
            AutoStartSwitch.IsOn = ViewModel.AutoStart;
            EnabledSwitch.IsOn = ViewModel.Enabled;

            UpdatePanels();
            UpdateRoutePreview();
            _suppress = false;

            BottomBar.PrimaryText = _loader.GetString("TunnelEdit_Save") ?? "保存";
            BottomBar.ShowOverflow = !ViewModel.IsNew;
            if (!ViewModel.IsNew)
            {
                var flyout = new MenuFlyout();
                var deleteItem = new MenuFlyoutItem
                {
                    Text = _loader.GetString("TunnelEdit_Delete") ?? "删除隧道"
                };
                deleteItem.Click += (s, e) => OnDeleteClick(s, EventArgs.Empty);
                flyout.Items.Add(deleteItem);
                BottomBar.OverflowFlyout = flyout;
            }

            ShowErrors();
        }

        private void UpdatePanels()
        {
            DestPanel.Visibility = ViewModel.IsDestVisible ? Visibility.Visible : Visibility.Collapsed;
            RelayPanel.Visibility = ViewModel.IsRelayVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateRoutePreview()
        {
            RoutePreviewText.Text = ViewModel.RoutePreview;
        }

        private void OnNameChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppress) return;
            ViewModel.Name = NameBox.Text;
            ShowErrors();
        }

        private void OnTypeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress) return;
            var selected = TypeBox.SelectedItem as TunnelTypeOption;
            if (selected != null)
            {
                ViewModel.Type = selected.Type;
                UpdatePanels();
                UpdateRoutePreview();
                ShowErrors();
            }
        }

        private void OnServerChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress) return;
            var selected = ServerBox.SelectedItem as IdNameOption;
            ViewModel.ServerId = selected?.Id ?? string.Empty;
            UpdateRoutePreview();
            ShowErrors();
        }

        private void OnGroupChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress) return;
            var selected = GroupBox.SelectedItem as IdNameOption;
            ViewModel.GroupId = selected?.Id ?? string.Empty;
        }

        private void OnListenHostChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppress) return;
            ViewModel.ListenHost = ListenHostBox.Text;
            UpdateRoutePreview();
            ShowErrors();
        }

        private void OnListenPortChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppress) return;
            ViewModel.ListenPortText = ListenPortBox.Text;
            UpdateRoutePreview();
            ShowErrors();
        }

        private void OnDestHostChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppress) return;
            ViewModel.DestHost = DestHostBox.Text;
            UpdateRoutePreview();
            ShowErrors();
        }

        private void OnDestPortChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppress) return;
            ViewModel.DestPortText = DestPortBox.Text;
            UpdateRoutePreview();
            ShowErrors();
        }

        private void OnDestServerChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress) return;
            var selected = DestServerBox.SelectedItem as IdNameOption;
            ViewModel.DestServerId = selected?.Id ?? string.Empty;
            UpdateRoutePreview();
            ShowErrors();
        }

        private void OnAutoReconnectToggled(object sender, RoutedEventArgs e)
        {
            if (_suppress) return;
            ViewModel.AutoReconnect = AutoReconnectSwitch.IsOn;
        }

        private void OnAutoStartToggled(object sender, RoutedEventArgs e)
        {
            if (_suppress) return;
            ViewModel.AutoStart = AutoStartSwitch.IsOn;
        }

        private void OnEnabledToggled(object sender, RoutedEventArgs e)
        {
            if (_suppress) return;
            ViewModel.Enabled = EnabledSwitch.IsOn;
        }

        private void ShowErrors()
        {
            ShowFieldError(NameError, ViewModel.GetError("name"));
            ShowFieldError(ServerError, ViewModel.GetError("serverId"));
            ShowFieldError(ListenHostError, ViewModel.GetError("listenHost"));
            ShowFieldError(ListenPortError, ViewModel.GetError("listenPort"));
            ShowFieldError(DestHostError, ViewModel.GetError("destHost"));
            ShowFieldError(DestPortError, ViewModel.GetError("destPort"));
            ShowFieldError(DestServerError, ViewModel.GetError("destServerId"));
        }

        private void ShowFieldError(TextBlock textBlock, string errorKey)
        {
            if (string.IsNullOrEmpty(errorKey))
            {
                textBlock.Visibility = Visibility.Collapsed;
                textBlock.Text = string.Empty;
            }
            else
            {
                textBlock.Text = _loader.GetString(errorKey) ?? errorKey;
                textBlock.Visibility = Visibility.Visible;
            }
        }

        private async void OnSaveClick(object sender, EventArgs e)
        {
            bool ok = await ViewModel.SaveAsync();
            if (ok)
            {
                NavigationService nav;
                if (ServiceRegistry.TryGet(out nav))
                {
                    nav.GoBack();
                }
            }
            else
            {
                ShowErrors();
            }
        }

        private async void OnDeleteClick(object sender, EventArgs e)
        {
            string title = _loader.GetString("Tunnels_DeleteConfirmTitle") ?? "删除隧道";
            string msg = _loader.GetString("TunnelEdit_DeleteConfirmMessage") ?? "确定要删除该隧道吗？";
            string deleteText = _loader.GetString("Tunnels_Delete") ?? "删除";
            string cancelText = _loader.GetString("Tunnels_Cancel") ?? "取消";

            var result = await ConfirmDialog.ShowAsync(title, msg, deleteText, cancelText, isDanger: true);
            if (result.Confirmed)
            {
                await ViewModel.DeleteAsync();
                NavigationService nav;
                if (ServiceRegistry.TryGet(out nav))
                {
                    nav.GoBack();
                }
            }
        }
    }
}
