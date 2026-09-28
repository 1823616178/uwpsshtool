using SshTool.App.Dialogs;
using SshTool.App.Infrastructure;
using SshTool.App.ViewModels;
using SshTool.Core.Appearance;
using SshTool.Core.Common;
using SshTool.Core.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views
{
    public sealed partial class AppearanceListPage : Page
    {
        // W06：页头返回按钮（与硬件返回键同一条处理链）。
        private void OnHeaderBackRequested(object sender, System.EventArgs e)
        {
            NavigationService nav;
            if (ServiceRegistry.TryGet(out nav))
            {
                nav.RequestBack();
            }
        }

        // R01 (C-02)：导航世代，离开后加载链不再触碰 UI。
        private readonly NavigationLifetime _lifetime = new NavigationLifetime();

        public AppearanceListPage()
        {
            ViewModel = new AppearanceListViewModel(AppServices.Current);
            this.InitializeComponent();
            AppearanceList.ItemsSource = ViewModel.Items;
        }

        public AppearanceListViewModel ViewModel { get; private set; }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            int generation = _lifetime.Begin();
            RefreshAsync(generation).Forget("AppearanceListPage.Refresh", AppLog.Logger);
        }

        // R01 (C-02)：世代保护加载链（当前 await 后无直接 UI 触碰，检查为后续维护兜底）。
        private async Task RefreshAsync(int generation)
        {
            try
            {
                await ViewModel.RefreshAsync();
                if (!_lifetime.IsCurrent(generation))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("AppearanceList", "外观列表加载失败", ex);
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            _lifetime.End();
            base.OnNavigatedFrom(e);
        }

        private void OnRowTapped(object sender, TappedRoutedEventArgs e)
        {
            var row = ((FrameworkElement)sender).DataContext as AppearanceRow;
            ViewModel.OpenEdit(row);
        }

        private async void OnSetDefaultClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var row = ((FrameworkElement)sender).DataContext as AppearanceRow;
                await ViewModel.SetDefaultAsync(row);
            }
            catch (Exception ex)
            {
                AppLog.Error("AppearanceListPage", "OnSetDefaultClick failed", ex);
            }
        }

        private void OnEditClick(object sender, RoutedEventArgs e)
        {
            var row = ((FrameworkElement)sender).DataContext as AppearanceRow;
            ViewModel.OpenEdit(row);
        }

        private async void OnDuplicateClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var row = ((FrameworkElement)sender).DataContext as AppearanceRow;
                await ViewModel.DuplicateAsync(row);
            }
            catch (Exception ex)
            {
                AppLog.Error("AppearanceListPage", "OnDuplicateClick failed", ex);
            }
        }

        private async void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var row = ((FrameworkElement)sender).DataContext as AppearanceRow;
                if (row == null)
                {
                    return;
                }
                if (row.IsBuiltIn)
                {
                    await ConfirmDialog.ShowAsync("无法删除", "内置主题不能删除，可以「复制为新主题」后再改。", "确定", "关闭");
                    return;
                }
                IReadOnlyList<Host> refs = await ViewModel.GetReferencingHostsAsync(row);
                int count = refs == null ? 0 : refs.Count;
                string message = "删除外观「" + (row.Name ?? string.Empty) + "」？";
                if (count > 0)
                {
                    message += "引用它的 " + count.ToString() + " 台主机将改回跟随默认。";
                }
                if (row.IsDefault)
                {
                    message += "它还是全局默认，删除后默认回到内置主题。";
                }
                ConfirmDialogResult confirm = await ConfirmDialog.ShowAsync(
                    "删除外观", message, "删除", "取消", true);
                if (!confirm.Confirmed)
                {
                    return;
                }
                await ViewModel.DeleteConfirmedAsync(row);
            }
            catch (Exception ex)
            {
                AppLog.Error("AppearanceListPage", "OnDeleteClick failed", ex);
            }
        }

        private void OnNewClick(object sender, RoutedEventArgs e)
        {
            ViewModel.NewCommand.Execute(null);
        }

        private async void OnImportClick(object sender, RoutedEventArgs e)
        {
            try
            {
                await ImportAsync();
            }
            catch (Exception ex)
            {
                AppLog.Error("AppearanceListPage", "OnImportClick failed", ex);
            }
        }

        // A04：配色导入（02-UI-DESIGN.md §5.12）。FileOpenPicker 选 .itermcolors /
        // .json → Core 解析（非法文件由解析器返回中文错误，不抛异常）→ 单个直接
        // 存并进编辑页改名微调，多个弹 ThemeSchemePickerDialog 让用户勾选。
        private async Task ImportAsync()
        {
            try
            {
                var picker = new FileOpenPicker();
                picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
                picker.FileTypeFilter.Add(".itermcolors");
                picker.FileTypeFilter.Add(".json");
                StorageFile file = await picker.PickSingleFileAsync();
                if (file == null)
                {
                    return;
                }
                var props = await file.GetBasicPropertiesAsync();
                if (props.Size > 262144)
                {
                    await ConfirmDialog.ShowAsync("导入失败", "文件过大（超过 256 KiB），请选择配色文件。", "确定", "关闭");
                    return;
                }
                string text = await FileIO.ReadTextAsync(file);
                string suggested = Path.GetFileNameWithoutExtension(file.Name);
                ThemeImportResult result;
                if (string.Equals(file.FileType, ".itermcolors", StringComparison.OrdinalIgnoreCase))
                {
                    result = ItermcolorsParser.Parse(text, suggested);
                }
                else if (string.Equals(file.FileType, ".json", StringComparison.OrdinalIgnoreCase))
                {
                    result = WindowsTerminalSchemeParser.Parse(text, suggested);
                }
                else
                {
                    await ConfirmDialog.ShowAsync("导入失败", "不支持的文件类型，请选择 .itermcolors 或 .json 文件。", "确定", "关闭");
                    return;
                }
                if (!result.Ok || result.Profiles.Count == 0)
                {
                    await ConfirmDialog.ShowAsync("导入失败",
                        result.Ok ? "文件中没有可导入的配色。" : result.Error, "确定", "关闭");
                    return;
                }
                if (result.Profiles.Count == 1)
                {
                    bool saved = await ViewModel.ImportProfilesAsync(new List<AppearanceProfile> { result.Profiles[0] });
                    if (!saved)
                    {
                        await ConfirmDialog.ShowAsync("导入失败", "保存导入的配色时出错，请重试。", "确定", "关闭");
                        return;
                    }
                    ViewModel.OpenEdit(new AppearanceRow(result.Profiles[0], false));
                    return;
                }
                IReadOnlyList<AppearanceProfile> selected =
                    await ThemeSchemePickerDialog.ShowAsync(result.Profiles);
                if (selected.Count == 0)
                {
                    return;
                }
                if (!await ViewModel.ImportProfilesAsync(selected))
                {
                    await ConfirmDialog.ShowAsync("导入失败", "保存导入的配色时出错，请重试。", "确定", "关闭");
                    return;
                }
                await ViewModel.RefreshAsync();
            }
            catch (Exception ex)
            {
                // 异常细节（类型/消息）只写日志，弹框只给一句能看懂的话。
                ILogger log;
                if (ServiceRegistry.TryGet(out log))
                {
                    try
                    {
                        log.Log(LogLevel.Error, "AppearanceList",
                            "导入配色失败：" + ex.GetType().Name + " " + ex.Message);
                    }
                    catch (Exception)
                    {
                    }
                }
                await ConfirmDialog.ShowAsync("导入失败", "读取或保存文件时出错，请重试。", "确定", "关闭");
            }
        }
    }
}
