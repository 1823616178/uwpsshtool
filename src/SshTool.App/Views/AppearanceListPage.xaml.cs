using SshTool.App.Dialogs;
using SshTool.App.Infrastructure;
using SshTool.App.ViewModels;
using SshTool.Core.Models;
using System.Collections.Generic;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views
{
    public sealed partial class AppearanceListPage : Page
    {
        public AppearanceListPage()
        {
            ViewModel = new AppearanceListViewModel(AppServices.Current);
            this.InitializeComponent();
            AppearanceList.ItemsSource = ViewModel.Items;
        }

        public AppearanceListViewModel ViewModel { get; private set; }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await ViewModel.RefreshAsync();
        }

        private void OnRowTapped(object sender, TappedRoutedEventArgs e)
        {
            var row = ((FrameworkElement)sender).DataContext as AppearanceRow;
            ViewModel.OpenEdit(row);
        }

        private async void OnSetDefaultClick(object sender, RoutedEventArgs e)
        {
            var row = ((FrameworkElement)sender).DataContext as AppearanceRow;
            await ViewModel.SetDefaultAsync(row);
        }

        private void OnEditClick(object sender, RoutedEventArgs e)
        {
            var row = ((FrameworkElement)sender).DataContext as AppearanceRow;
            ViewModel.OpenEdit(row);
        }

        private async void OnDuplicateClick(object sender, RoutedEventArgs e)
        {
            var row = ((FrameworkElement)sender).DataContext as AppearanceRow;
            await ViewModel.DuplicateAsync(row);
        }

        private async void OnDeleteClick(object sender, RoutedEventArgs e)
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

        private void OnNewClick(object sender, RoutedEventArgs e)
        {
            ViewModel.NewCommand.Execute(null);
        }

        private void OnImportClick(object sender, RoutedEventArgs e)
        {
            ViewModel.ImportCommand.Execute(null);
        }
    }
}
