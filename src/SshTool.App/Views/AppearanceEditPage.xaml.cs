using System;
using System.Globalization;
using SshTool.App.Controls;
using SshTool.App.Dialogs;
using SshTool.App.Infrastructure;
using SshTool.App.Terminal;
using SshTool.App.ViewModels;
using SshTool.Core.Appearance;
using SshTool.Core.Common;
using SshTool.Core.Models;
using SshTool.Core.Terminal;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views
{
    public sealed class AppearanceEditArgs
    {
        public string AppearanceId { get; private set; }

        public static AppearanceEditArgs New()
        {
            return new AppearanceEditArgs();
        }

        public static AppearanceEditArgs Edit(string appearanceId)
        {
            return new AppearanceEditArgs { AppearanceId = appearanceId };
        }
    }

    // A03：外观编辑页（02-UI-DESIGN.md §5.12）。草稿未保存即丢弃（= 还原）；
    // 每次改动同步重建预览（36×8 格，远 < 100 ms）。
    public sealed partial class AppearanceEditPage : Page, IBackHandler
    {
        private readonly StaticTerminalScreen _screen = new StaticTerminalScreen();
        // R01 (C-02)：导航世代，离开后加载链不再触碰 UI。
        private readonly NavigationLifetime _lifetime = new NavigationLifetime();
        // ANSI 调色板外层触控格的透明填充：Button 的 Background 为 null 时整格不参与命中，
        // 内层色块让出的间隙就会漏点击；透明填充补齐 TouchTargetMin，且无可见底色。
        private readonly Brush _touchFill = new SolidColorBrush(Windows.UI.Colors.Transparent);
        private bool _abandonConfirmed;
        private bool _suppress;

        public AppearanceEditPage()
        {
            ViewModel = new AppearanceEditViewModel(AppServices.Current);
            this.InitializeComponent();
            FontSizeSlider.Minimum = AppearanceEditViewModel.MinFontSize;
            FontSizeSlider.Maximum = AppearanceEditViewModel.MaxFontSize;
            FontSizeSlider.StepFrequency = 1;
            LineHeightSlider.Minimum = AppearanceEditViewModel.MinLineHeight;
            LineHeightSlider.Maximum = AppearanceEditViewModel.MaxLineHeight;
            LineHeightSlider.StepFrequency = 0.05;
            PaddingSlider.Minimum = AppearanceEditViewModel.MinPadding;
            PaddingSlider.Maximum = AppearanceEditViewModel.MaxPadding;
            PaddingSlider.StepFrequency = 1;
            Preview.Screen = _screen;
            Preview.Renderer.Focused = true;
            FgPicker.ColorChanged += (s, e) => SetDraftColor("foreground", FgPicker.Color);
            BgPicker.ColorChanged += (s, e) => SetDraftColor("background", BgPicker.Color);
            CursorPicker.ColorChanged += (s, e) => SetDraftColor("cursor", CursorPicker.Color);
            SelectionPicker.ColorChanged += (s, e) => SetDraftColor("selection", SelectionPicker.Color);
        }

        public AppearanceEditViewModel ViewModel { get; private set; }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            int generation = _lifetime.Begin();
            NavigationService nav;
            if (ServiceRegistry.TryGet(out nav))
            {
                nav.RegisterBackHandler(this);
            }
            var args = e.Parameter as AppearanceEditArgs;
            LoadAsync(generation, args == null ? null : args.AppearanceId).Forget("AppearanceEditPage.Load", AppLog.Logger);
        }

        // R01 (C-02)：await 后先查世代，页面已离开则不再 BindLoaded 触碰 XAML。
        private async System.Threading.Tasks.Task LoadAsync(int generation, string appearanceId)
        {
            try
            {
                await ViewModel.LoadAsync(appearanceId);
                if (!_lifetime.IsCurrent(generation))
                {
                    return;
                }
                BindLoaded();
            }
            catch (Exception ex)
            {
                AppLog.Error("AppearanceEdit", "外观编辑页加载失败", ex);
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
            ConfirmAbandonAsync().Forget("AppearanceEditPage.ConfirmAbandon", AppLog.Logger);
            return true;
        }

        private async System.Threading.Tasks.Task ConfirmAbandonAsync()
        {
            ConfirmDialogResult result = await ConfirmDialog.ShowAsync(
                "放弃修改？", "未保存的更改将丢失，预览会还原。", "放弃", "继续编辑");
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

        private void BindLoaded()
        {
            AppearanceProfile draft = ViewModel.Draft;
            if (draft == null)
            {
                return;
            }
            _suppress = true;
            TitleText.Text = ViewModel.Title;
            NameBox.Text = draft.Name ?? string.Empty;
            FontSizeSlider.Value = draft.FontSize;
            LineHeightSlider.Value = draft.LineHeight;
            PaddingSlider.Value = draft.Padding;
            BoldWeightSwitch.IsOn = draft.FontWeightBold;
            BoldBrightSwitch.IsOn = draft.BoldAsBright;
            CursorBlinkSwitch.IsOn = draft.CursorBlink;
            UpdateLabels(draft);
            SetColorButton(FgButton, draft.Foreground);
            SetColorButton(BgButton, draft.Background);
            SetColorButton(CursorButton, draft.Cursor);
            SetColorButton(SelectionButton, draft.Selection);
            SelectCursorStyle(draft.CursorStyle);
            RebuildPaletteButtons(draft);
            _suppress = false;
            ShowErrors();
            RefreshPreview();
        }

        private void UpdateLabels(AppearanceProfile draft)
        {
            FontSizeLabel.Text = Localized.Format("AppearanceEdit_FontSize", "字号 {0}", draft.FontSize);
            LineHeightLabel.Text = Localized.Format("AppearanceEdit_LineHeight", "行高 {0}", draft.LineHeight.ToString("0.0", CultureInfo.CurrentCulture));
            PaddingLabel.Text = Localized.Format("AppearanceEdit_Padding", "内边距 {0}", draft.Padding);
        }

        private void RefreshPreview()
        {
            AppearanceProfile draft = ViewModel.Draft;
            if (draft == null)
            {
                return;
            }
            try
            {
                SampleScreen sample = SampleScreenBuilder.Build(draft);
                _screen.SetScreen(sample.Cells, sample.Cols, sample.Rows, sample.CursorRow, sample.CursorCol);
                Preview.ApplyAppearance(draft);
            }
            catch (Exception)
            {
            }
        }

        private void SetColorButton(Button button, string hex)
        {
            if (button == null)
            {
                return;
            }
            button.Background = AppearanceBrushes.FromHex(hex);
            button.Content = hex ?? string.Empty;
        }

        private void RebuildPaletteButtons(AppearanceProfile draft)
        {
            PaletteGrid.Children.Clear();
            if (draft.Palette == null)
            {
                return;
            }
            double target = (double)Application.Current.Resources["TouchTargetMin"];
            Thickness thin = (Thickness)Application.Current.Resources["BorderThin"];
            CornerRadius radius = (CornerRadius)Application.Current.Resources["RadiusSm"];
            Thickness gap = new Thickness(ColorSwatchPicker.SwatchGap);
            // AppBorderBrush 在 ThemeDictionaries 里，索引器取不到（恒 null）→ 用 Banner 的解析。
            Brush hairline = Banner.ResolveThemedBrush("AppBorderBrush");
            for (int i = 0; i < draft.Palette.Count && i < 16; i++)
            {
                int index = i;
                // 内层：圆角色块 + 序号，四周让出 SwatchGap 与邻块分开（走查 R-C 分层）。
                var swatch = new Border
                {
                    Background = AppearanceBrushes.FromHex(draft.Palette[index]),
                    CornerRadius = radius,
                    Margin = gap,
                    BorderThickness = thin,
                    BorderBrush = hairline,
                    Child = new TextBlock
                    {
                        // 不设字号/前景：沿用 Button 向下继承的正文样式，与原先 string 内容一致。
                        Text = index.ToString(CultureInfo.InvariantCulture),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
                // 外层 Button：纯触控格（透明填充保命中、无圆角无描边），Flyout 挂在它上面。
                var button = new Button
                {
                    Background = _touchFill,
                    Content = swatch,
                    MinWidth = target,
                    MinHeight = target,
                    Margin = (Thickness)Application.Current.Resources["BorderNone"],
                    Padding = (Thickness)Application.Current.Resources["PadNone"],
                    BorderThickness = (Thickness)Application.Current.Resources["BorderNone"],
                    Tag = index
                };
                var picker = new ColorSwatchPicker
                {
                    Color = draft.Palette[index],
                    OriginalColor = draft.Palette[index]
                };
                picker.ColorChanged += (s, e) =>
                {
                    if (ViewModel.Draft == null || ViewModel.Draft.Palette == null
                        || index >= ViewModel.Draft.Palette.Count)
                    {
                        return;
                    }
                    ViewModel.Draft.Palette[index] = picker.Color;
                    swatch.Background = AppearanceBrushes.FromHex(picker.Color);
                    OnDraftChanged();
                };
                button.Flyout = new Flyout { Content = picker };
                PaletteGrid.Children.Add(button);
            }
        }

        private void OnDraftChanged()
        {
            if (ViewModel.Draft != null)
            {
                UpdateLabels(ViewModel.Draft);
            }
            ViewModel.Touch();
            ViewModel.Validate();
            ShowErrors();
            RefreshPreview();
        }

        private void SetDraftColor(string field, string hex)
        {
            if (_suppress || ViewModel.Draft == null || string.IsNullOrEmpty(hex))
            {
                return;
            }
            if (field == "foreground")
            {
                ViewModel.Draft.Foreground = hex;
                SetColorButton(FgButton, hex);
            }
            else if (field == "background")
            {
                ViewModel.Draft.Background = hex;
                SetColorButton(BgButton, hex);
            }
            else if (field == "cursor")
            {
                ViewModel.Draft.Cursor = hex;
                SetColorButton(CursorButton, hex);
            }
            else if (field == "selection")
            {
                ViewModel.Draft.Selection = hex;
                SetColorButton(SelectionButton, hex);
            }
            else
            {
                return;
            }
            OnDraftChanged();
        }

        private void SelectCursorStyle(CursorStyle style)
        {
            string want = style == CursorStyle.Bar ? "bar" : (style == CursorStyle.Underline ? "underline" : "block");
            for (int i = 0; i < CursorBox.Items.Count; i++)
            {
                var item = CursorBox.Items[i] as ComboBoxItem;
                if (item != null && string.Equals((string)item.Tag, want, StringComparison.Ordinal))
                {
                    CursorBox.SelectedIndex = i;
                    return;
                }
            }
            CursorBox.SelectedIndex = 0;
        }

        private async void OnSaveClick(object sender, RoutedEventArgs e)
        {
            try
            {
                bool ok = await ViewModel.SaveAsync();
                if (ok)
                {
                    NavigationService nav;
                    if (ServiceRegistry.TryGet(out nav))
                    {
                        nav.GoBack();
                    }
                    return;
                }
                ShowErrors();
                if (ViewModel.Errors != null && ViewModel.Errors.ContainsKey("save"))
                {
                    // 「save」里是 VM 兜到的原始异常（细节已入日志），不一句红字糊到用户脸上；
                    // 这里只给一句能照着做的中文。
                    await ConfirmDialog.ShowAsync(
                        "保存失败", "这份配色没能保存，请检查名称与颜色设置后重试。", "确定", "关闭");
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("AppearanceEditPage", "OnSaveClick failed", ex);
            }
        }

        private void OnResetClick(object sender, RoutedEventArgs e)
        {
            ViewModel.ResetToBaseline();
            BindLoaded();
        }

        private async void OnDuplicateClick(object sender, RoutedEventArgs e)
        {
            try
            {
                string id = await ViewModel.DuplicateCurrentAsync();
                if (string.IsNullOrEmpty(id))
                {
                    ShowErrors();
                    return;
                }
                await ViewModel.LoadAsync(id);
                BindLoaded();
            }
            catch (Exception ex)
            {
                AppLog.Error("AppearanceEditPage", "OnDuplicateClick failed", ex);
            }
        }

        private void OnNameChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppress || ViewModel.Draft == null)
            {
                return;
            }
            ViewModel.Draft.Name = NameBox.Text;
            OnDraftChanged();
        }

        private void OnFontSizeChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_suppress || ViewModel.Draft == null)
            {
                return;
            }
            ViewModel.Draft.FontSize = (int)Math.Round(e.NewValue);
            OnDraftChanged();
        }

        private void OnLineHeightChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_suppress || ViewModel.Draft == null)
            {
                return;
            }
            ViewModel.Draft.LineHeight = Math.Round(e.NewValue, 2);
            OnDraftChanged();
        }

        private void OnPaddingChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_suppress || ViewModel.Draft == null)
            {
                return;
            }
            ViewModel.Draft.Padding = (int)Math.Round(e.NewValue);
            OnDraftChanged();
        }

        private void OnBoldWeightToggled(object sender, RoutedEventArgs e)
        {
            if (_suppress || ViewModel.Draft == null)
            {
                return;
            }
            ViewModel.Draft.FontWeightBold = BoldWeightSwitch.IsOn;
            OnDraftChanged();
        }

        private void OnBoldBrightToggled(object sender, RoutedEventArgs e)
        {
            if (_suppress || ViewModel.Draft == null)
            {
                return;
            }
            ViewModel.Draft.BoldAsBright = BoldBrightSwitch.IsOn;
            OnDraftChanged();
        }

        private void OnCursorBlinkToggled(object sender, RoutedEventArgs e)
        {
            if (_suppress || ViewModel.Draft == null)
            {
                return;
            }
            ViewModel.Draft.CursorBlink = CursorBlinkSwitch.IsOn;
            OnDraftChanged();
        }

        private void OnCursorStyleChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress || ViewModel.Draft == null)
            {
                return;
            }
            var item = CursorBox.SelectedItem as ComboBoxItem;
            string tag = item == null ? null : (string)item.Tag;
            if (tag == "bar")
            {
                ViewModel.Draft.CursorStyle = CursorStyle.Bar;
            }
            else if (tag == "underline")
            {
                ViewModel.Draft.CursorStyle = CursorStyle.Underline;
            }
            else
            {
                ViewModel.Draft.CursorStyle = CursorStyle.Block;
            }
            OnDraftChanged();
        }

        private void OnFgFlyoutOpening(object sender, object e)
        {
            SyncPicker(FgPicker, "foreground");
        }

        private void OnBgFlyoutOpening(object sender, object e)
        {
            SyncPicker(BgPicker, "background");
        }

        private void OnCursorFlyoutOpening(object sender, object e)
        {
            SyncPicker(CursorPicker, "cursor");
        }

        private void OnSelectionFlyoutOpening(object sender, object e)
        {
            SyncPicker(SelectionPicker, "selection");
        }

        private void SyncPicker(ColorSwatchPicker picker, string field)
        {
            if (picker == null || ViewModel.Draft == null)
            {
                return;
            }
            string hex = null;
            if (field == "foreground")
            {
                hex = ViewModel.Draft.Foreground;
            }
            else if (field == "background")
            {
                hex = ViewModel.Draft.Background;
            }
            else if (field == "cursor")
            {
                hex = ViewModel.Draft.Cursor;
            }
            else if (field == "selection")
            {
                hex = ViewModel.Draft.Selection;
            }
            if (!string.IsNullOrEmpty(hex))
            {
                picker.OriginalColor = hex;
                picker.Color = hex;
            }
        }

        private void ShowErrors()
        {
            ClearError(NameError);
            ClearError(FontError);
            ClearError(ColorError);
            ClearError(PaletteError);
            SetError(NameError, "name");
            SetError(FontError, "fontSize");
            SetError(FontError, "lineHeight");
            SetError(FontError, "padding");
            SetError(ColorError, "foreground");
            SetError(ColorError, "background");
            SetError(ColorError, "cursor");
            SetError(ColorError, "selection");
            SetError(PaletteError, "palette");
        }

        private static void ClearError(TextBlock block)
        {
            if (block != null)
            {
                block.Text = string.Empty;
                block.Visibility = Visibility.Collapsed;
            }
        }

        private void SetError(TextBlock block, string field)
        {
            if (block == null || ViewModel.Errors == null)
            {
                return;
            }
            // 同一栏位多条错误时保留第一条（SetError 按字段逐个调用，后调不覆盖已有文案）。
            if (block.Visibility == Visibility.Visible && block.Text.Length > 0)
            {
                return;
            }
            string message;
            if (ViewModel.Errors.TryGetValue(field, out message))
            {
                block.Text = message;
                block.Visibility = Visibility.Visible;
            }
            else
            {
                block.Text = string.Empty;
                block.Visibility = Visibility.Collapsed;
            }
        }
    }
}
