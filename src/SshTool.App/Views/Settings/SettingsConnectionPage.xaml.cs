using System;
using System.Globalization;
using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using SshTool.App.Platform;
using SshTool.App.ViewModels;
using SshTool.Core.Common;
using SshTool.Core.Storage;
using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views.Settings
{
    public sealed partial class SettingsConnectionPage : Page
    {
        private const string ReswTimes = "Settings_Unit_Times";
        private const string ReswSeconds = "Settings_Unit_Seconds";
        private const string ReswNever = "Settings_Unit_Never";
        private const string ReswReconnectDesc = "Settings_Reconnect_Description";
        private const string ReswTimeoutDesc = "Settings_Timeout_Description";
        private const string ReswAgentTimeoutDesc = "Settings_AgentTimeout_Description";

        private readonly ResourceLoader _loader = ResourceLoader.GetForCurrentView();
        private readonly NavigationLifetime _lifetime = new NavigationLifetime();

        private DispatcherTimer _debounceTimer;
        private Slider _pendingSlider;
        private bool _suppress;
        private bool _isDragging;

        public SettingsConnectionPage()
        {
            ViewModel = new SettingsViewModel(AppServices.Current);
            this.InitializeComponent();

            _suppress = true;
            ApplyRange(ReconnectSlider, "reconnectMaxAttempts");
            ApplyRange(TimeoutSlider, "connectTimeoutSeconds");
            ApplyRange(AgentTimeoutSlider, "agentKeyTimeoutMinutes");
            _suppress = false;

            _debounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _debounceTimer.Tick += OnDebounceTick;
            BindAll();
        }

        public SettingsViewModel ViewModel { get; private set; }

        private static void ApplyRange(Slider slider, string key)
        {
            SettingDefinition def = SettingDefinitions.Require(key);
            slider.Maximum = def.MaxValue.Value;
            slider.Minimum = def.MinValue.Value;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            int generation = _lifetime.Begin();
            BindAll();
            RefreshAppLockAvailabilityAsync(generation).Forget("SettingsConnectionPage.AppLockAvailability", AppLog.Logger);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            CommitPendingSlider();
            _lifetime.End();
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
                KeepScreenBox.Items.Clear();
                KeepScreenBox.Items.Add(Load("Settings_Connection_KeepScreen_Never"));
                KeepScreenBox.Items.Add(Load("Settings_Connection_KeepScreen_Session"));
                KeepScreenBox.Items.Add(Load("Settings_Connection_KeepScreen_Always"));
                KeepScreenBox.SelectedIndex = SettingsViewModel.KeepScreenOnToIndex(ViewModel.KeepScreenOn);

                KeepAliveSwitch.IsOn = ViewModel.KeepAliveInBackground;
                NotifySwitch.IsOn = ViewModel.NotifyOnDisconnect;
                AppLockSwitch.IsOn = ViewModel.AppLockEnabled;

                BgDiscBox.Items.Clear();
                BgDiscBox.Items.Add(Load("Settings_Connection_BgDisc_Off"));
                BgDiscBox.Items.Add(Load("Settings_Connection_BgDisc_5"));
                BgDiscBox.Items.Add(Load("Settings_Connection_BgDisc_15"));
                BgDiscBox.Items.Add(Load("Settings_Connection_BgDisc_30"));
                BgDiscBox.Items.Add(Load("Settings_Connection_BgDisc_60"));
                BgDiscBox.SelectedIndex =
                    SettingsViewModel.BackgroundDisconnectToIndex(ViewModel.BackgroundDisconnectMinutes);

                ReconnectSection.Header = Load("Settings_Connection_Reconnect_Header");
                ReconnectSlider.Value = ViewModel.ReconnectMaxAttempts;
                UpdateReconnectDisplay(ViewModel.ReconnectMaxAttempts);

                TimeoutSection.Header = Load("Settings_Connection_Timeout_Header");
                TimeoutSlider.Value = ViewModel.ConnectTimeoutSeconds;
                UpdateTimeoutDisplay(ViewModel.ConnectTimeoutSeconds);

                AgentTimeoutSection.Header = Load("Settings_Connection_AgentTimeout_Header");
                AgentTimeoutSlider.Value = ViewModel.AgentKeyTimeoutMinutes;
                UpdateAgentTimeoutDisplay(ViewModel.AgentKeyTimeoutMinutes);
            }
            finally
            {
                _suppress = false;
            }
        }

        private void UpdateReconnectDisplay(int attempts)
        {
            ReconnectValue.Text = string.Format(CultureInfo.InvariantCulture, Load(ReswTimes), attempts);
            ReconnectSection.Description = string.Format(
                CultureInfo.InvariantCulture, Load(ReswReconnectDesc),
                string.Format(CultureInfo.InvariantCulture, Load(ReswTimes), attempts));
        }

        private void UpdateTimeoutDisplay(int seconds)
        {
            TimeoutValue.Text = string.Format(CultureInfo.InvariantCulture, Load(ReswSeconds), seconds);
            TimeoutSection.Description = string.Format(
                CultureInfo.InvariantCulture, Load(ReswTimeoutDesc),
                string.Format(CultureInfo.InvariantCulture, Load(ReswSeconds), seconds));
        }

        private void UpdateAgentTimeoutDisplay(int minutes)
        {
            string text = minutes <= 0
                ? Load(ReswNever)
                : string.Format(CultureInfo.InvariantCulture, Load("Settings_Unit_Minutes"), minutes);
            AgentTimeoutValue.Text = text;
            AgentTimeoutSection.Description = string.Format(
                CultureInfo.InvariantCulture, Load(ReswAgentTimeoutDesc), text);
        }

        private void ScheduleSliderCommit(Slider slider)
        {
            if (_isDragging) { return; }
            _pendingSlider = slider;
            _debounceTimer.Stop();
            _debounceTimer.Start();
        }

        private void OnDebounceTick(object sender, object e)
        {
            _debounceTimer.Stop();
            CommitPendingSlider();
        }

        private void CommitPendingSlider()
        {
            _debounceTimer.Stop();
            if (_pendingSlider == null) { return; }
            Slider slider = _pendingSlider;
            _pendingSlider = null;
            try
            {
                if (slider == ReconnectSlider)
                {
                    ViewModel.ReconnectMaxAttempts = (int)Math.Round(ReconnectSlider.Value);
                }
                else if (slider == TimeoutSlider)
                {
                    ViewModel.ConnectTimeoutSeconds = (int)Math.Round(TimeoutSlider.Value);
                }
                else if (slider == AgentTimeoutSlider)
                {
                    ViewModel.AgentKeyTimeoutMinutes = (int)Math.Round(AgentTimeoutSlider.Value);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("SettingsConnectionPage", "commit-slider", ex);
            }
        }

        private void OnSliderManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
        {
            _isDragging = true;
        }

        private void CompleteManipulation(Slider slider)
        {
            _isDragging = false;
            CommitPendingSlider();
        }

        private void OnReconnectChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_suppress) { return; }
            int attempts = (int)Math.Round(e.NewValue);
            UpdateReconnectDisplay(attempts);
            ScheduleSliderCommit(ReconnectSlider);
        }

        private void OnReconnectManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
        {
            CompleteManipulation(ReconnectSlider);
        }

        private void OnTimeoutChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_suppress) { return; }
            int seconds = (int)Math.Round(e.NewValue);
            UpdateTimeoutDisplay(seconds);
            ScheduleSliderCommit(TimeoutSlider);
        }

        private void OnTimeoutManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
        {
            CompleteManipulation(TimeoutSlider);
        }

        private void OnAgentTimeoutChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_suppress) { return; }
            int minutes = (int)Math.Round(e.NewValue);
            UpdateAgentTimeoutDisplay(minutes);
            ScheduleSliderCommit(AgentTimeoutSlider);
        }

        private void OnAgentTimeoutManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
        {
            CompleteManipulation(AgentTimeoutSlider);
        }

        private void OnResetReconnectClick(object sender, RoutedEventArgs e)
        {
            CommitPendingSlider();
            ViewModel.ReconnectMaxAttempts = SettingsViewModel.DefaultReconnectAttempts;
            ReconnectSlider.Value = ViewModel.ReconnectMaxAttempts;
            UpdateReconnectDisplay(ViewModel.ReconnectMaxAttempts);
        }

        private void OnResetTimeoutClick(object sender, RoutedEventArgs e)
        {
            CommitPendingSlider();
            ViewModel.ConnectTimeoutSeconds = SettingsViewModel.DefaultConnectTimeout;
            TimeoutSlider.Value = ViewModel.ConnectTimeoutSeconds;
            UpdateTimeoutDisplay(ViewModel.ConnectTimeoutSeconds);
        }

        private void OnResetAgentTimeoutClick(object sender, RoutedEventArgs e)
        {
            CommitPendingSlider();
            ViewModel.AgentKeyTimeoutMinutes = SettingsViewModel.DefaultAgentKeyTimeout;
            AgentTimeoutSlider.Value = ViewModel.AgentKeyTimeoutMinutes;
            UpdateAgentTimeoutDisplay(ViewModel.AgentKeyTimeoutMinutes);
        }

        private void OnKeepScreenChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress || KeepScreenBox.SelectedIndex < 0) { return; }
            ViewModel.KeepScreenOn = SettingsViewModel.IndexToKeepScreenOn(KeepScreenBox.SelectedIndex);
            Toast.Show(Load("Settings_Toast_KeepScreenChanged"));
        }

        private void OnKeepAliveToggled(object sender, RoutedEventArgs e)
        {
            if (_suppress) { return; }
            ViewModel.KeepAliveInBackground = KeepAliveSwitch.IsOn;
        }

        private void OnNotifyToggled(object sender, RoutedEventArgs e)
        {
            if (_suppress) { return; }
            ViewModel.NotifyOnDisconnect = NotifySwitch.IsOn;
        }

        private async Task RefreshAppLockAvailabilityAsync(int generation)
        {
            bool available = await AppLockService.IsAvailableAsync();
            if (!_lifetime.IsCurrent(generation))
            {
                return;
            }
            AppLockSwitch.IsEnabled = available || AppLockSwitch.IsOn;
            if (!available)
            {
                AppLockNote.Text = Load("Settings_Security_AppLock_Unavailable");
            }
        }

        private void OnAppLockToggled(object sender, RoutedEventArgs e)
        {
            if (_suppress) { return; }
            if (!AppLockSwitch.IsOn)
            {
                ViewModel.AppLockEnabled = false;
                return;
            }
            ConfirmAppLockAsync(_lifetime.Current).Forget("SettingsConnectionPage.ConfirmAppLock", AppLog.Logger);
        }

        private async Task ConfirmAppLockAsync(int generation)
        {
            bool verified = await AppLockService.VerifyAsync(Load("Settings_Security_AppLock_Confirm"));
            if (verified)
            {
                ViewModel.AppLockEnabled = true;
                return;
            }
            if (_lifetime.IsCurrent(generation))
            {
                _suppress = true;
                AppLockSwitch.IsOn = false;
                _suppress = false;
            }
        }

        private void OnBgDiscChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress || BgDiscBox.SelectedIndex < 0) { return; }
            ViewModel.BackgroundDisconnectMinutes =
                SettingsViewModel.IndexToBackgroundDisconnect(BgDiscBox.SelectedIndex);
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
