using System;
using SshTool.App.Controls;
using SshTool.App.Infrastructure;
using SshTool.App.ViewModels;
using SshTool.Core.Sftp;
using Windows.ApplicationModel.Resources;
using Windows.Foundation;
using Windows.UI.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views
{
    // F03：SFTP 页面（02-UI-DESIGN.md §5.17）。ViewModel 承担全部业务逻辑；
    // 本类只做：导航参数装配、绑定装配、行长按/右键菜单（按类型动态构建）。
    public sealed partial class SftpPage : Page
    {
        private static readonly ResourceLoader Loader = ResourceLoader.GetForCurrentView();

        public SftpPage()
        {
            this.InitializeComponent();
        }

        public SftpViewModel ViewModel { get; private set; }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ViewModel = new SftpViewModel(e.Parameter as SftpArgs ?? new SftpArgs());
            ViewModel.NotifyRequested += OnNotifyRequested;
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            EntryList.ItemsSource = ViewModel.Rows;
            BreadcrumbBar.ItemsSource = ViewModel.Breadcrumbs;
            TransferList.ItemsSource = ViewModel.TransferRows;
            UpdateChrome();
            await ViewModel.LoadAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.NotifyRequested -= OnNotifyRequested;
                ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
                ViewModel.Leave();
                ViewModel = null;
            }
            base.OnNavigatedFrom(e);
        }

        private void OnViewModelPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            UpdateChrome();
        }

        // 标题 / 空目录 / 错误 / 传输面板 / 忙遮罩 全部跟随 ViewModel。
        private void UpdateChrome()
        {
            SftpViewModel vm = ViewModel;
            if (vm == null)
            {
                return;
            }
            TitleText.Text = string.IsNullOrEmpty(vm.Title)
                ? Loader.GetString("Sftp_Title.Text")
                : vm.Title;
            BusyOverlay.IsActive = vm.IsBusy;
            BusyOverlay.Message = vm.BusyMessage;

            bool empty = vm.IsEmpty && !vm.HasError;
            EmptyPanel.Visibility = empty || vm.HasError ? Visibility.Visible : Visibility.Collapsed;
            if (vm.HasError)
            {
                EmptyPanel.Title = Loader.GetString("Sftp_LoadFailed.Text");
                EmptyPanel.Description = vm.StatusText;
                EmptyPanel.PrimaryText = Loader.GetString("Sftp_Retry.Text");
                EmptyPanel.PrimaryCommand = vm.RefreshCommand;
            }
            else if (empty)
            {
                EmptyPanel.Title = Loader.GetString("Sftp_EmptyTitle.Text");
                EmptyPanel.Description = Loader.GetString("Sftp_EmptyDescription.Text");
                EmptyPanel.PrimaryText = null;
                EmptyPanel.PrimaryCommand = null;
            }

            TransferPanel.Visibility = vm.ShowTransferBar ? Visibility.Visible : Visibility.Collapsed;
            TransferHeaderText.Text = vm.TransferHeader;
            ClearFinishedButton.Content = Loader.GetString("Sftp_ClearFinished");
            ChevronText.Text = vm.TransfersExpanded ? "▾" : "▸";
            TransferList.Visibility = vm.TransfersExpanded ? Visibility.Visible : Visibility.Collapsed;
            // 面板可见但一行都没有（清除已完成之后、队列回调之前等瞬态）时给空状态说明，
            // 免得只剩一个标题栏。整条面板的显隐条件（ShowTransferBar）不变。
            bool transfersEmpty = vm.TransferRows.Count == 0;
            TransfersEmptyText.Visibility = transfersEmpty ? Visibility.Visible : Visibility.Collapsed;
            if (transfersEmpty)
            {
                TransfersEmptyText.Text = Loader.GetString("Sftp_TransfersEmpty");
            }
        }

        private void OnNotifyRequested(object sender, string message)
        {
            Toast.Show(message ?? string.Empty);
        }

        // ---- CommandBar ----

        private void OnUploadClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.UploadCommand.Execute(null);
            }
        }

        private void OnNewFolderClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.NewFolderCommand.Execute(null);
            }
        }

        private void OnRefreshClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.RefreshCommand.Execute(null);
            }
        }

        // 「更多」：排序方式（名称/大小/时间，选中项打勾）+ 显示隐藏文件开关（§5.17）。
        private void OnMoreClick(object sender, RoutedEventArgs e)
        {
            SftpViewModel vm = ViewModel;
            if (vm == null)
            {
                return;
            }
            var flyout = new MenuFlyout();
            flyout.Items.Add(HeaderItem(Loader.GetString("Sftp_SortHeader")));
            AddSortItem(flyout, Loader.GetString("Sftp_SortName"), SftpSortMode.Name, vm);
            AddSortItem(flyout, Loader.GetString("Sftp_SortSize"), SftpSortMode.Size, vm);
            AddSortItem(flyout, Loader.GetString("Sftp_SortMtime"), SftpSortMode.Mtime, vm);
            flyout.Items.Add(new MenuFlyoutSeparator());
            var hidden = new ToggleMenuFlyoutItem
            {
                Text = Loader.GetString("Sftp_ShowHidden"),
                IsChecked = vm.ShowHidden
            };
            hidden.Click += (s, a) => vm.ToggleHiddenCommand.Execute(null);
            flyout.Items.Add(hidden);
            FrameworkElement anchor = sender as FrameworkElement;
            if (anchor != null)
            {
                flyout.ShowAt(anchor);
            }
        }

        private void AddSortItem(MenuFlyout flyout, string text, SftpSortMode mode, SftpViewModel vm)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = text,
                IsChecked = vm.SortMode == mode
            };
            item.Click += (s, a) => vm.SetSortCommand.Execute(mode);
            flyout.Items.Add(item);
        }

        private static MenuFlyoutItem HeaderItem(string text)
        {
            return new MenuFlyoutItem { Text = text, IsEnabled = false };
        }

        // ---- 面包屑 / 列表 ----

        private void OnBreadcrumbClick(object sender, RoutedEventArgs e)
        {
            var crumb = (sender as FrameworkElement)?.DataContext as SftpBreadcrumb;
            if (crumb != null && ViewModel != null)
            {
                ViewModel.NavigateCommand.Execute(crumb);
            }
        }

        private void OnUpClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.UpCommand.Execute(null);
            }
        }

        private void OnEntryClick(object sender, ItemClickEventArgs e)
        {
            var row = e.ClickedItem as SftpRowVm;
            if (row != null && ViewModel != null)
            {
                ViewModel.EnterRowCommand.Execute(row);
            }
        }

        // ---- 行菜单（长按 / 右键；§5.17：下载、重命名、权限、删除、复制路径） ----

        private void OnRowRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            ShowRowMenu(sender as FrameworkElement, e.GetPosition(sender as UIElement));
            e.Handled = true;
        }

        private void OnRowHolding(object sender, HoldingRoutedEventArgs e)
        {
            if (e.HoldingState == HoldingState.Started)
            {
                ShowRowMenu(sender as FrameworkElement, null);
            }
            e.Handled = true;
        }

        private void ShowRowMenu(FrameworkElement anchor, Point? point)
        {
            var row = anchor != null ? anchor.DataContext as SftpRowVm : null;
            SftpViewModel vm = ViewModel;
            if (row == null || row.Entry == null || vm == null)
            {
                return;
            }
            var flyout = new MenuFlyout();
            if (row.Entry.IsSymlink && !string.IsNullOrEmpty(row.Entry.LinkTarget))
            {
                string targetPath = ResolveLinkTargetPath(row);
                flyout.Items.Add(Item(Loader.GetString("Sftp_LinkMenuOpenTarget"),
                    () => vm.NavigateCommand.Execute(new SftpBreadcrumb(
                        row.Entry.LinkTarget, targetPath))));
                if (!row.IsDirectory)
                {
                    // UI 走查：目录版有「进入目标」，链接文件版缺对应的取目标动作
                    // （普通「下载」走链接自身路径）。补上「下载目标」＝按解析后的
                    // 目标路径下载。目标大小未知（-1）：传输行不画进度条，只显已传字节。
                    SftpRowVm targetRow = MakeLinkTargetRow(row, targetPath);
                    flyout.Items.Add(Item(Loader.GetString("Sftp_LinkFileMenuOpenTarget"),
                        () => vm.DownloadRowCommand.Execute(targetRow)));
                }
            }
            if (!row.IsDirectory)
            {
                flyout.Items.Add(Item(Loader.GetString("Sftp_RowDownload"),
                    () => vm.DownloadRowCommand.Execute(row)));
            }
            flyout.Items.Add(Item(Loader.GetString("Sftp_RowRename"),
                () => vm.RenameRowCommand.Execute(row)));
            flyout.Items.Add(Item(Loader.GetString("Sftp_RowPermissions"),
                () => vm.PermissionsRowCommand.Execute(row)));
            flyout.Items.Add(new MenuFlyoutSeparator());
            // UI 走查：删除是破坏性项，菜单里给红字，与 ConfirmDialog(isDanger) 呼应。
            MenuFlyoutItem delete = Item(Loader.GetString("Sftp_RowDelete"),
                () => vm.DeleteRowCommand.Execute(row));
            delete.Style = (Style)Application.Current.Resources["DangerMenuItemStyle"];
            flyout.Items.Add(delete);
            flyout.Items.Add(Item(Loader.GetString("Sftp_RowCopyPath"),
                () => vm.CopyPathCommand.Execute(row)));
            if (point.HasValue)
            {
                flyout.ShowAt(anchor, point.Value);
            }
            else
            {
                flyout.ShowAt(anchor);
            }
        }

        // 符号链接目标可为相对路径（ls 语义）；不绝对则按所在目录拼接。
        private static string ResolveLinkTargetPath(SftpRowVm row)
        {
            string target = row.Entry.LinkTarget ?? string.Empty;
            if (RemotePath.IsAbsolute(target))
            {
                return target;
            }
            return RemotePath.Combine(RemotePath.GetDirectoryName(row.Entry.Path), target);
        }

        // 「下载目标」的行参数：只喂 DownloadRowCommand，不进任何列表。
        // 名称取目标末段（另存对话框的默认名），大小/权限/时间给哨兵 -1（未知）。
        private static SftpRowVm MakeLinkTargetRow(SftpRowVm row, string targetPath)
        {
            // 目标可能是根（link → "/"）：末段为空时退回链接自身名，
            // 否则 FileSavePicker.SuggestedFileName 为空会直接抛异常。
            string name = RemotePath.GetFileName(targetPath);
            if (string.IsNullOrEmpty(name) && row.Entry != null)
            {
                name = row.Entry.Name;
            }
            var entry = new RemoteEntry(name, targetPath,
                RemoteEntryType.File, -1, -1, DateTime.MinValue, string.Empty);
            return new SftpRowVm(entry, string.Empty, string.Empty, string.Empty);
        }

        // ---- 传输面板 ----

        private void OnToggleTransfersClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.ToggleTransfersCommand.Execute(null);
            }
        }

        private void OnCancelTransferClick(object sender, RoutedEventArgs e)
        {
            var row = ResolveTransferRow(sender);
            if (row != null && ViewModel != null)
            {
                ViewModel.CancelTransferCommand.Execute(row);
            }
        }

        private void OnRetryTransferClick(object sender, RoutedEventArgs e)
        {
            var row = ResolveTransferRow(sender);
            if (row != null && ViewModel != null)
            {
                ViewModel.RetryTransferCommand.Execute(row);
            }
        }

        private void OnClearFinishedClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.ClearFinishedCommand.Execute(null);
            }
        }

        // ItemsControl 模板内的按钮：沿可视树找到 ContentPresenter 取行对象。
        private static TransferRowVm ResolveTransferRow(object sender)
        {
            DependencyObject node = sender as DependencyObject;
            while (node != null && !(node is ContentPresenter))
            {
                node = VisualTreeHelper.GetParent(node);
            }
            var presenter = node as ContentPresenter;
            return presenter != null ? presenter.Content as TransferRowVm : null;
        }

        private static MenuFlyoutItem Item(string text, Action action)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (s, a) => action();
            return item;
        }
    }

    // ---- 转换器（页面局部） ----

    public sealed class NonEmptyToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            string text = value as string;
            return string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotSupportedException();
        }
    }

    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return value is bool && (bool)value ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotSupportedException();
        }
    }

    // 进度总量未知（-1）时不画进度条。
    public sealed class HasPercentToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return value is double && (double)value >= 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotSupportedException();
        }
    }
}
