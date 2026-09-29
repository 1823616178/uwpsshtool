using System;
using System.Globalization;
using SshTool.App.Infrastructure;
using SshTool.App.ViewModels;
using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views.Settings
{
    public sealed partial class SettingsKeyboardPage : Page
    {
        private readonly ResourceLoader _loader = ResourceLoader.GetForCurrentView();
        private bool _suppress;

        public SettingsKeyboardPage()
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
                KeyBarVisibleSwitch.IsOn = ViewModel.KeyBarVisible;
                KeyBarLayoutSection.Header = Load("Settings_Keyboard_KeyBarLayout_Header");
                KeyBarSummary.Text = FormatKeyBarSummary();
                ShortcutsSection.Header = Load("Settings_Keyboard_Shortcuts_Header");
                ShortcutSummary.Text = FormatShortcutSummary();
            }
            finally
            {
                _suppress = false;
            }
        }

        private string FormatKeyBarSummary()
        {
            int count = SshTool.Core.Terminal.KeyBarLayout.Parse(ViewModel.KeyBarLayout).Count;
            return string.Format(CultureInfo.InvariantCulture, "{0} {1}", count, Load("Settings_Unit_Keys"));
        }

        private string FormatShortcutSummary()
        {
            var map = ViewModel.CurrentShortcutMap;
            if (map.Conflicts.Count > 0)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0} {1}", map.Conflicts.Count, Load("Settings_Unit_Conflicts"));
            }
            return string.Format(CultureInfo.InvariantCulture, "{0} {1}", map.Bindings.Count, Load("Settings_Unit_Actions"));
        }

        private void OnKeyBarVisibleToggled(object sender, RoutedEventArgs e)
        {
            if (_suppress) { return; }
            ViewModel.KeyBarVisible = KeyBarVisibleSwitch.IsOn;
        }

        private void OnEditKeyBarClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(KeyBarLayoutEditorPage));
        }

        private void OnEditShortcutsClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(ShortcutEditorPage));
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
