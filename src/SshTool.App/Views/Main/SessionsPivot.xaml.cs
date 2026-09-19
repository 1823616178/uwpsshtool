using SshTool.App.ViewModels;
using SshTool.Core.Sessions;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Views.Main
{
    public sealed partial class SessionsPivot : UserControl
    {
        public SessionsPivot()
        {
            this.InitializeComponent();
        }

        public SessionsPaneViewModel ViewModel { get; private set; }

        public void Attach(SessionsPaneViewModel vm)
        {
            ViewModel = vm;
            if (vm == null)
            {
                return;
            }
            SessionList.ItemsSource = vm.Items;
            vm.PropertyChanged += (s, e) => UpdateChrome();
            UpdateChrome();
            var ignore = vm.LoadRestoreAsync();
        }

        private void UpdateChrome()
        {
            if (ViewModel == null)
            {
                return;
            }
            RestoreCard.Visibility = ViewModel.HasRestore ? Visibility.Visible : Visibility.Collapsed;
            RestoreText.Text = "上次未关闭 " + ViewModel.RestoreCount.ToString() + " 个会话";
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
}
