using System;
using System.Globalization;
using System.Threading.Tasks;
using SshTool.App.Infrastructure;
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
    public sealed partial class SettingsTerminalPage : Page
    {
        private const string AppearanceFallbackResw = "Settings_AppearanceFallback";
        private const string ReswPt = "Settings_Unit_Pt";
        private const string ReswLines = "Settings_Unit_Lines";
        private const string ReswFontSizeDesc = "Settings_FontSize_Description";
        private const string ReswScrollbackDesc = "Settings_Scrollback_Description";

        private readonly ResourceLoader _loader = ResourceLoader.GetForCurrentView();
        private readonly NavigationLifetime _lifetime = new NavigationLifetime();

        private DispatcherTimer _debounceTimer;
        private Slider _pendingSlider;
        private bool _suppress;
        private bool _isDragging;
        private string _appearanceName = string.Empty;

        public SettingsTerminalPage()
        {
            ViewModel = new SettingsViewModel(AppServices.Current);
            this.InitializeComponent();

            _suppress = true;
            ApplyRange(FontSlider, "terminalFontSize");
            ApplyRange(ScrollbackSlider, "scrollbackLines");
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
            RefreshAppearanceNameAsync(generation).Forget("SettingsTerminalPage.RefreshAppearanceName", AppLog.Logger);
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
                AppearanceSection.Header = Load("Settings_Terminal_Appearance_Header");
                AppearanceValue.Text = string.IsNullOrEmpty(_appearanceName)
                    ? Load(AppearanceFallbackResw) : _appearanceName;

                FontSection.Header = Load("Settings_Terminal_FontSize_Header");
                FontSlider.Value = ViewModel.TerminalFontSize;
                UpdateFontSizeDisplay(ViewModel.TerminalFontSize);

                ScrollbackSection.Header = Load("Settings_Terminal_Scrollback_Header");
                ScrollbackSlider.Value = ViewModel.ScrollbackLines;
                UpdateScrollbackDisplay(ViewModel.ScrollbackLines);

                AltScrollBox.Items.Clear();
                AltScrollBox.Items.Add(Load("Settings_Terminal_AltScroll_Arrows"));
                AltScrollBox.Items.Add(Load("Settings_Terminal_AltScroll_Wheel"));
                AltScrollBox.SelectedIndex = SettingsViewModel.AltScrollToIndex(ViewModel.AltScreenScroll);

                PasteConfirmSwitch.IsOn = ViewModel.PasteConfirmMultiline;

                BellBox.Items.Clear();
                BellBox.Items.Add(Load("Settings_Terminal_Bell_Vibrate"));
                BellBox.Items.Add(Load("Settings_Terminal_Bell_Visual"));
                BellBox.Items.Add(Load("Settings_Terminal_Bell_None"));
                BellBox.SelectedIndex = SettingsViewModel.BellModeToIndex(ViewModel.BellMode);
            }
            finally
            {
                _suppress = false;
            }
        }

        private void UpdateFontSizeDisplay(int size)
        {
            FontValue.Text = string.Format(CultureInfo.InvariantCulture, Load(ReswPt), size);
            FontSection.Description = string.Format(
                CultureInfo.InvariantCulture, Load(ReswFontSizeDesc),
                string.Format(CultureInfo.InvariantCulture, Load(ReswPt), size));
        }

        private void UpdateScrollbackDisplay(int lines)
        {
            ScrollbackValue.Text = string.Format(CultureInfo.InvariantCulture, Load(ReswLines), lines);
            ScrollbackSection.Description = string.Format(
                CultureInfo.InvariantCulture, Load(ReswScrollbackDesc),
                string.Format(CultureInfo.InvariantCulture, Load(ReswLines), lines));
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
                if (slider == FontSlider)
                {
                    ViewModel.TerminalFontSize = (int)Math.Round(FontSlider.Value);
                }
                else if (slider == ScrollbackSlider)
                {
                    ViewModel.ScrollbackLines = (int)Math.Round(ScrollbackSlider.Value);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("SettingsTerminalPage", "commit-slider", ex);
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

        private void OnFontChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_suppress) { return; }
            int size = (int)Math.Round(e.NewValue);
            UpdateFontSizeDisplay(size);
            ScheduleSliderCommit(FontSlider);
        }

        private void OnFontManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
        {
            CompleteManipulation(FontSlider);
        }

        private void OnScrollbackChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_suppress) { return; }
            int lines = (int)Math.Round(e.NewValue);
            UpdateScrollbackDisplay(lines);
            ScheduleSliderCommit(ScrollbackSlider);
        }

        private void OnScrollbackManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
        {
            CompleteManipulation(ScrollbackSlider);
        }

        private void OnResetFontClick(object sender, RoutedEventArgs e)
        {
            CommitPendingSlider();
            ViewModel.TerminalFontSize = SettingsViewModel.DefaultFontSize;
            FontSlider.Value = ViewModel.TerminalFontSize;
            UpdateFontSizeDisplay(ViewModel.TerminalFontSize);
        }

        private void OnResetScrollbackClick(object sender, RoutedEventArgs e)
        {
            CommitPendingSlider();
            ViewModel.ScrollbackLines = SettingsViewModel.DefaultScrollback;
            ScrollbackSlider.Value = ViewModel.ScrollbackLines;
            UpdateScrollbackDisplay(ViewModel.ScrollbackLines);
        }

        private void OnAltScrollChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress || AltScrollBox.SelectedIndex < 0) { return; }
            ViewModel.AltScreenScroll = SettingsViewModel.IndexToAltScroll(AltScrollBox.SelectedIndex);
        }

        private void OnPasteConfirmToggled(object sender, RoutedEventArgs e)
        {
            if (_suppress) { return; }
            ViewModel.PasteConfirmMultiline = PasteConfirmSwitch.IsOn;
        }

        private void OnBellChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress || BellBox.SelectedIndex < 0) { return; }
            ViewModel.BellMode = SettingsViewModel.IndexToBellMode(BellBox.SelectedIndex);
        }

        private void OnAppearanceClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(AppearanceListPage));
        }

        private async Task RefreshAppearanceNameAsync(int generation)
        {
            string id = ViewModel.DefaultAppearanceId;
            string resolved = Load(AppearanceFallbackResw);
            if (!string.IsNullOrEmpty(id))
            {
                try
                {
                    System.Collections.Generic.IReadOnlyList<SshTool.Core.Models.AppearanceProfile> all =
                        await AppServices.Current.AppearanceService.ListAsync().ConfigureAwait(true);
                    for (int i = 0; i < all.Count; i++)
                    {
                        if (string.Equals(all[i].Id, id, StringComparison.Ordinal)
                            && !string.IsNullOrEmpty(all[i].Name))
                        {
                            resolved = all[i].Name;
                            break;
                        }
                    }
                }
                catch (Exception)
                {
                    resolved = Load(AppearanceFallbackResw);
                }
            }
            if (!_lifetime.IsCurrent(generation))
            {
                return;
            }
            _appearanceName = resolved;
            if (AppearanceValue != null)
            {
                AppearanceValue.Text = resolved;
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
