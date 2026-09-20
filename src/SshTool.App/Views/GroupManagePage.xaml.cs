using SshTool.App.Controls;
using SshTool.App.Infrastructure;
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
        private bool _suppress;

        public GroupManagePage()
        {
            ViewModel = new GroupManageViewModel(AppServices.Current);
            this.InitializeComponent();
            GroupList.ItemsSource = ViewModel.Groups;
        }

        public GroupManageViewModel ViewModel { get; private set; }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            var ignore = ViewModel.RefreshAsync();
        }

        private void OnAddClick(object sender, RoutedEventArgs e)
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
            var brush = Banner.ResolveThemedBrush("AppAccentBrush") as SolidColorBrush;
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
            var ignore = ViewModel.RenameAsync(row, NameBox.Text);
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
            var ignore = ViewModel.SetColorAsync(row, Picker.Color);
        }

        private void OnMoveUp(object sender, RoutedEventArgs e)
        {
            var row = GroupList.SelectedItem as GroupRow;
            if (row != null)
            {
                var ignore = ViewModel.MoveAsync(row, -1);
            }
        }

        private void OnMoveDown(object sender, RoutedEventArgs e)
        {
            var row = GroupList.SelectedItem as GroupRow;
            if (row != null)
            {
                var ignore = ViewModel.MoveAsync(row, 1);
            }
        }

        private void OnDelete(object sender, RoutedEventArgs e)
        {
            var row = GroupList.SelectedItem as GroupRow;
            if (row != null)
            {
                var ignore = ViewModel.DeleteAsync(row);
            }
        }
    }
}
