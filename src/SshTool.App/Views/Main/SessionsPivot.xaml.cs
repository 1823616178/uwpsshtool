using System;
using System.ComponentModel;
using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using SshTool.App.ViewModels;
using SshTool.Core.Common;
using SshTool.Core.Sessions;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Data;

namespace SshTool.App.Views.Main
{
    public sealed partial class SessionsPivot : UserControl
    {
        public SessionsPivot()
        {
            this.InitializeComponent();
        }

        public SessionsPaneViewModel ViewModel { get; private set; }

        // O03：vm 是 ServiceRegistry 单例（MainViewModel 注册），而本控件随
        // MainPage 每次导航重建。原来这里挂的是匿名 lambda——永远解不掉，
        // 于是单例的订阅表里每访问一次首页就多攥住一棵死掉的可视化树。
        // 改具名处理器 + Detach；重复 Attach 先拆旧的，保证不叠加。
        public void Attach(SessionsPaneViewModel vm)
        {
            if (ReferenceEquals(ViewModel, vm))
            {
                return;
            }
            Detach();
            ViewModel = vm;
            if (vm == null)
            {
                return;
            }
            SessionList.ItemsSource = vm.Items;
            vm.PropertyChanged += OnViewModelPropertyChanged;
            UpdateChrome();
            vm.LoadRestoreAsync().Forget("SessionsPivot.LoadRestore", AppLog.Logger);
        }

        // 页面 OnNavigatedFrom 调用。ItemsSource 也要断开：单例的
        // ObservableCollection 会经 CollectionChanged 攥住这个 ListView。幂等。
        public void Detach()
        {
            if (ViewModel != null)
            {
                ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
                ViewModel = null;
            }
            SessionList.ItemsSource = null;
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            UpdateChrome();
        }

        private void UpdateChrome()
        {
            if (ViewModel == null)
            {
                return;
            }
            RestoreCard.Visibility = ViewModel.HasRestore ? Visibility.Visible : Visibility.Collapsed;
            RestoreText.Text = Localized.Format("SessionsPivot_Restore", "上次未关闭 {0} 个会话", ViewModel.RestoreCount);
            bool empty = ViewModel.Items.Count == 0 && !ViewModel.HasRestore;
            Empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            SessionList.Visibility = ViewModel.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnRestore(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.RestoreCommand.Execute(null);
            }
        }

        private void OnItemClick(object sender, ItemClickEventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.Open(e.ClickedItem as SessionInfo);
            }
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            var btn = sender as FrameworkElement;
            if (ViewModel != null && btn != null)
            {
                ViewModel.Close(btn.Tag as SessionInfo);
            }
        }
    }

    // UI 走查（验收项 3）：SessionUiState 是枚举，直接绑定会把英文枚举名甩到屏幕上。
    // 这里只做显示层转换（说法与 StatusDot 的无障碍名一致），不参与任何连接判断。
    public sealed class SessionStateTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (!(value is SessionUiState))
            {
                return string.Empty;
            }
            switch ((SessionUiState)value)
            {
                case SessionUiState.Connecting:
                    return "连接中";
                case SessionUiState.Authenticating:
                    return "认证中";
                case SessionUiState.Connected:
                    return "已连接";
                case SessionUiState.Reconnecting:
                    return "重连中";
                case SessionUiState.Disconnected:
                    return "已断开";
                case SessionUiState.Error:
                    return "连接错误";
                case SessionUiState.Closed:
                    return "已关闭";
                default:
                    return "未连接";
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotSupportedException();
        }
    }
}
