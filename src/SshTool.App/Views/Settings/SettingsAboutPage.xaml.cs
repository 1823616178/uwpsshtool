using System;
using SshTool.App.Infrastructure;
using SshTool.App.ViewModels;
using SshTool.Core.Common;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views.Settings
{
    public sealed partial class SettingsAboutPage : Page
    {
        private readonly ResourceLoader _loader = ResourceLoader.GetForCurrentView();
        private readonly NavigationLifetime _lifetime = new NavigationLifetime();
        private bool _suppress;

        public SettingsAboutPage()
        {
            ViewModel = new SettingsViewModel(AppServices.Current);
            this.InitializeComponent();
            ProbeList.ItemsSource = ViewModel.Probes;
            BindAll();
        }

        public SettingsViewModel ViewModel { get; private set; }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _lifetime.Begin();
            ViewModel.RefreshDiagnostics();
            BindAll();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
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
                VersionSection.Header = Load("Settings_About_Version_Header");
                AppVersionValue.Text = ViewModel.AppVersionText;

                LogLevelBox.Items.Clear();
                LogLevelBox.Items.Add(Load("Settings_About_LogLevel_Debug"));
                LogLevelBox.Items.Add(Load("Settings_About_LogLevel_Info"));
                LogLevelBox.Items.Add(Load("Settings_About_LogLevel_Warn"));
                LogLevelBox.Items.Add(Load("Settings_About_LogLevel_Error"));
                LogLevelBox.SelectedIndex = SettingsViewModel.LogLevelToIndex(ViewModel.LogLevel);

                DiagnosticsSection.Header = Load("Settings_About_Diagnostics_Header");
                BindDiagnostics();
            }
            finally
            {
                _suppress = false;
            }
        }

        private void BindDiagnostics()
        {
            OsVersionValue.Text = string.Format(
                Load("Settings_Diagnostics_OsVersion"), ViewModel.OsVersionText);
            DeviceFamilyValue.Text = string.Format(
                Load("Settings_Diagnostics_DeviceFamily"), ViewModel.DeviceFamilyText);
            MemoryValue.Text = string.Format(
                Load("Settings_Diagnostics_Memory"), ViewModel.MemoryText);
        }

        private void OnLicensesClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(LicensesPage));
        }

        private void OnLogLevelChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress || LogLevelBox.SelectedIndex < 0) { return; }
            ViewModel.LogLevel = SettingsViewModel.IndexToLogLevel(LogLevelBox.SelectedIndex);
            Toast.Show(Load("Settings_Toast_LogLevelChanged"));
        }

        private async void OnExportLogsClick(object sender, RoutedEventArgs e)
        {
            try
            {
                int generation = _lifetime.Current;
                string result = await ViewModel.ExportLogsAsync();
                if (!_lifetime.IsCurrent(generation))
                {
                    return;
                }
                Toast.Show(result);
            }
            catch (Exception ex)
            {
                AppLog.Error("SettingsAboutPage", "OnExportLogsClick failed", ex);
            }
        }

        private async void OnClearLogsClick(object sender, RoutedEventArgs e)
        {
            try
            {
                int generation = _lifetime.Current;
                var confirm = await Dialogs.ConfirmDialog.ShowAsync(
                    Load("Settings_About_ClearLogs"),
                    Load("Settings_Confirm_ClearLogs"),
                    Load("Settings_About_ClearLogs"),
                    Load("Dialog_Cancel"), true);
                if (!confirm.Confirmed)
                {
                    return;
                }
                string result = await ViewModel.ClearLogsAsync();
                if (!_lifetime.IsCurrent(generation))
                {
                    return;
                }
                Toast.Show(result);
            }
            catch (Exception ex)
            {
                AppLog.Error("SettingsAboutPage", "OnClearLogsClick failed", ex);
            }
        }

        private void OnRefreshDiagClick(object sender, RoutedEventArgs e)
        {
            ViewModel.RefreshDiagnostics();
            AppVersionValue.Text = ViewModel.AppVersionText;
            BindDiagnostics();
            Toast.Show(Load("Settings_Toast_DiagnosticsRefreshed"));
        }

        private void OnCopyDiagClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
                package.SetText(ViewModel.BuildDiagnosticsText());
                Clipboard.SetContent(package);
                Toast.Show(Load("Settings_Toast_DiagnosticsCopied"));
            }
            catch (Exception)
            {
                Toast.Show(Load("Settings_Toast_CopyFailed"));
            }
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
