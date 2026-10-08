using SshTool.App.Platform;
using System;
using SshTool.App.Controls;
using SshTool.App.Infrastructure;
using SshTool.Core.Common;
using SshTool.App.ViewModels;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views
{
    public sealed partial class GroupManagePage : Page
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

        private bool _suppress;

        public GroupManagePage()
        {
            ViewModel = new GroupManageViewModel(AppServices.Current);
            this.InitializeComponent();
            GroupList.ItemsSource = ViewModel.Groups;
            Empty.Title = Localized.Get("GroupManage_EmptyTitle", "暂无分组");
            Empty.Description = Localized.Get("GroupManage_EmptyDescription", "创建分组整理主机，支持自定义名称和强调色。");
            Empty.PrimaryText = Localized.Get("GroupManage_AddGroup", "新建分组");
            Empty.PrimaryClick += (s, e) => OnAddClick(s, EventArgs.Empty);
            BottomBar.PrimaryText = Localized.Get("GroupManage_AddGroup", "新建分组");
        }

        public GroupManageViewModel ViewModel { get; private set; }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ViewModel.Groups.CollectionChanged += OnGroupsCollectionChanged;
            ViewModel.RefreshAsync().Forget("GroupManagePage.Refresh", AppLog.Logger);
            UpdateEmptyVisibility();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            ViewModel.Groups.CollectionChanged -= OnGroupsCollectionChanged;
            base.OnNavigatedFrom(e);
        }

        private void OnGroupsCollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            UpdateEmptyVisibility();
        }

        private void UpdateEmptyVisibility()
        {
            bool empty = ViewModel.Groups.Count == 0;
            Empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            GroupList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnAddClick(object sender, EventArgs e)
        {
            ViewModel.AddCommand.Execute(null);
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var row = GroupList.SelectedItem as GroupRow;
            Editor.Visibility = row == null ? Visibility.Collapsed : Visibility.Visible;
            if (row == null)
            {
                return;
            }
            _suppress = true;
            NameBox.Text = row.Name ?? string.Empty;
            string color = row.Color;
            if (string.IsNullOrEmpty(color))
            {
                // UI 走查：未设色的分组用应用强调色（随主题解析），不再写死蓝色。
                color = AccentColorHex();
            }
            if (!string.IsNullOrEmpty(color))
            {
                Picker.Color = color;
            }
            _suppress = false;
        }

        // 取当前主题的 AppAccentBrush → #RRGGBB；解析不到时返回 null（选择器保留自身默认值）。
        private static string AccentColorHex()
        {
            var brush = ThemeService.ResolveBrush("AppAccentBrush") as SolidColorBrush;
            if (brush == null)
            {
                return null;
            }
            Color c = brush.Color;
            return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        }

        private void OnNameLostFocus(object sender, RoutedEventArgs e)
        {
            if (_suppress)
            {
                return;
            }
            var row = GroupList.SelectedItem as GroupRow;
            if (row == null)
            {
                return;
            }
            ViewModel.RenameAsync(row, NameBox.Text).Forget("GroupManagePage.Rename", AppLog.Logger);
        }

        private void OnColorChanged(object sender, System.EventArgs e)
        {
            if (_suppress)
            {
                return;
            }
            var row = GroupList.SelectedItem as GroupRow;
            if (row == null)
            {
                return;
            }
            ViewModel.SetColorAsync(row, Picker.Color).Forget("GroupManagePage.SetColor", AppLog.Logger);
        }

        private void OnMoveUp(object sender, RoutedEventArgs e)
        {
            var row = GroupList.SelectedItem as GroupRow;
            if (row != null)
            {
                ViewModel.MoveAsync(row, -1).Forget("GroupManagePage.Move", AppLog.Logger);
            }
        }

        private void OnMoveDown(object sender, RoutedEventArgs e)
        {
            var row = GroupList.SelectedItem as GroupRow;
            if (row != null)
            {
                ViewModel.MoveAsync(row, 1).Forget("GroupManagePage.Move", AppLog.Logger);
            }
        }

        private void OnDelete(object sender, RoutedEventArgs e)
        {
            var row = GroupList.SelectedItem as GroupRow;
            if (row != null)
            {
                ViewModel.DeleteAsync(row).Forget("GroupManagePage.Delete", AppLog.Logger);
            }
        }
    }
}
