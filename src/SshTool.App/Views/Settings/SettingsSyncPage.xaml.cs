using System;
using SshTool.App.Infrastructure;
using SshTool.App.ViewModels;
using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views.Settings
{
    public sealed partial class SettingsSyncPage : Page
    {
        private readonly ResourceLoader _loader = ResourceLoader.GetForCurrentView();
        private bool _suppress;

        public SettingsSyncPage()
        {
            ViewModel = new SettingsViewModel(AppServices.Current);
            this.InitializeComponent();
            BindAll();
        }

        public SettingsViewModel ViewModel { get; private set; }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            BindAll();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            ViewModel.Detach();
            base.OnNavigatedFrom(e);
        }

        private string Load(string key)
        {
            try { return _loader.GetString(key); }
            catch (Exception) { return string.Empty; }
        }

        private void BindAll()
        {
            _suppress = true;
            try
            {
                PollBox.Items.Clear();
                PollBox.Items.Add(Load("Settings_Connection_Poll_Off"));
                PollBox.Items.Add(Load("Settings_Connection_Poll_1"));
                PollBox.Items.Add(Load("Settings_Connection_Poll_5"));
                PollBox.Items.Add(Load("Settings_Connection_Poll_15"));
                PollBox.SelectedIndex =
                    SettingsViewModel.SyncPollToIndex(ViewModel.SyncPollForegroundSeconds);
            }
            finally
            {
                _suppress = false;
            }
        }

        private void OnPollChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress || PollBox.SelectedIndex < 0) { return; }
            ViewModel.SyncPollForegroundSeconds = SettingsViewModel.IndexToSyncPoll(PollBox.SelectedIndex);
        }

        private void OnOpenAccountSyncClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(Views.Sync.AccountSyncPage));
        }

        private void OnHeaderBackRequested(object sender, EventArgs e)
        {
            NavigationService nav;
            if (ServiceRegistry.TryGet(out nav))
            {
                nav.RequestBack();
            }
            else if (Frame != null && Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }
    }
}
