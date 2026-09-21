using SshTool.App.ViewModels;
using SshTool.Core.Sessions;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Controls
{
    public sealed partial class SessionsPane : UserControl
    {
        public SessionsPane()
        {
            this.InitializeComponent();
        }

        public SessionsPaneViewModel ViewModel { get; private set; }

        // O03：vm 是 ServiceRegistry 单例，本控件随 TerminalPage 重建。
        // 这里没挂事件，但 ItemsSource 同样会让单例的 ObservableCollection
        // 经 CollectionChanged 攥住这个 ListView，必须在离开时断开。
        public void Attach(SessionsPaneViewModel vm)
        {
            ViewModel = vm;
            if (vm != null)
            {
                List.ItemsSource = vm.Items;
            }
        }

        // 页面 OnNavigatedFrom 调用。幂等。
        public void Detach()
        {
            ViewModel = null;
            List.ItemsSource = null;
        }

        private void OnNew(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.NewSessionCommand.Execute(null);
            }
        }

        private void OnItemClick(object sender, ItemClickEventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.Open(e.ClickedItem as SessionInfo);
            }
        }
    }
}
