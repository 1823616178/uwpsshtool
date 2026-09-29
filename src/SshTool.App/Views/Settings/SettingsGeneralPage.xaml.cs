using System;
using SshTool.App.Infrastructure;
using SshTool.App.ViewModels;
using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views.Settings
{
    public sealed partial class SettingsGeneralPage : Page
    {
        private readonly ResourceLoader _loader = ResourceLoader.GetForCurrentView();
        private bool _suppress;

        public SettingsGeneralPage()
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
                ThemeBox.Items.Clear();
                ThemeBox.Items.Add(Load("Settings_General_Theme_System"));
                ThemeBox.Items.Add(Load("Settings_General_Theme_Dark"));
                ThemeBox.Items.Add(Load("Settings_General_Theme_Light"));
                ThemeBox.SelectedIndex = SettingsViewModel.ThemeModeToIndex(ViewModel.ThemeMode);
                AccentSwitch.IsOn = ViewModel.UseSystemAccent;

                SortBox.Items.Clear();
                SortBox.Items.Add(Load("Settings_General_Sort_Name"));
                SortBox.Items.Add(Load("Settings_General_Sort_Recent"));
                SortBox.SelectedIndex = SettingsViewModel.SortModeToIndex(ViewModel.HostSortMode);
                QuickConnectSwitch.IsOn = ViewModel.ShowQuickConnect;
                HapticsSwitch.IsOn = ViewModel.HapticsEnabled;

                LanguageBox.Items.Clear();
                LanguageBox.Items.Add(Load("Settings_General_Language_System"));
                LanguageBox.Items.Add(Load("Settings_General_Language_Zh"));
                LanguageBox.Items.Add(Load("Settings_General_Language_En"));
                LanguageBox.SelectedIndex = SettingsViewModel.LanguageToIndex(ViewModel.Language);
            }
            finally
            {
                _suppress = false;
            }
        }

        private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress || ThemeBox.SelectedIndex < 0) { return; }
            ViewModel.ThemeMode = SettingsViewModel.IndexToThemeMode(ThemeBox.SelectedIndex);
            Toast.Show(Load("Settings_Toast_ThemeChanged"));
        }

        private void OnAccentToggled(object sender, RoutedEventArgs e)
        {
            if (_suppress) { return; }
            ViewModel.UseSystemAccent = AccentSwitch.IsOn;
            Toast.Show(Load("Settings_Toast_AccentChanged"));
        }

        private void OnSortChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress || SortBox.SelectedIndex < 0) { return; }
            ViewModel.HostSortMode = SettingsViewModel.IndexToSortMode(SortBox.SelectedIndex);
        }

        private void OnQuickConnectToggled(object sender, RoutedEventArgs e)
        {
            if (_suppress) { return; }
            ViewModel.ShowQuickConnect = QuickConnectSwitch.IsOn;
        }

        private void OnHapticsToggled(object sender, RoutedEventArgs e)
        {
            if (_suppress) { return; }
            ViewModel.HapticsEnabled = HapticsSwitch.IsOn;
        }

        private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress || LanguageBox.SelectedIndex < 0) { return; }
            ViewModel.Language = SettingsViewModel.IndexToLanguage(LanguageBox.SelectedIndex);
            Toast.Show(Load("Settings_Toast_LanguageChanged"));
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
