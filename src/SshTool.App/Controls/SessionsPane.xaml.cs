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

        public void Attach(SessionsPaneViewModel vm)
        {
            ViewModel = vm;
            if (vm != null)
            {
                List.ItemsSource = vm.Items;
            }
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
