using SshTool.App.Infrastructure;
using SshTool.App.ViewModels;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Views.Debug
{
    // V02（05 §6.1）：开发工具页——原 MainPage 溢出的 9 个调试入口。页面本身始终编译
    // （与其余 Debug 页一致），入口可见性由 MainPage 按 DEBUG_PAGES 控制。
    public sealed partial class DevToolsPage : Page
    {
        private HostListViewModel _hostsVm;

        public DevToolsPage()
        {
            this.InitializeComponent();
        }

        private void OnBackRequested(object sender, System.EventArgs e)
        {
            NavigationService nav;
            if (ServiceRegistry.TryGet(out nav) && nav.CanGoBack)
            {
                nav.GoBack();
                return;
            }
            if (Frame != null && Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }

        private void OnDebugConnectClick(object sender, System.EventArgs e)
        {
            Frame.Navigate(typeof(DebugConnectPage));
        }

        private void OnVaultClick(object sender, System.EventArgs e)
        {
            Frame.Navigate(typeof(VaultSelfCheckPage));
        }

        private void OnSpikeClick(object sender, System.EventArgs e)
        {
            Frame.Navigate(typeof(SpikePage));
        }

        private void OnTokenGalleryClick(object sender, System.EventArgs e)
        {
            Frame.Navigate(typeof(TokenGalleryPage));
        }

        private void OnRenderSpikeClick(object sender, System.EventArgs e)
        {
            Frame.Navigate(typeof(RenderSpikePage));
        }

        private void OnPerfClick(object sender, System.EventArgs e)
        {
            Frame.Navigate(typeof(PerfPage));
        }

        private void OnInputSpikeClick(object sender, System.EventArgs e)
        {
            Frame.Navigate(typeof(InputSpikePage));
        }

        private void OnPlatformSpikeClick(object sender, System.EventArgs e)
        {
            Frame.Navigate(typeof(PlatformSpikePage));
        }

        private void OnGenerateHostsClick(object sender, System.EventArgs e)
        {
            // 与 PerfPage 同款：独立 HostListViewModel，只借 GenerateTestHostsCommand，不动主页面 VM。
            if (_hostsVm == null && AppServices.Current != null)
            {
                _hostsVm = new HostListViewModel(AppServices.Current);
            }
            if (_hostsVm != null && _hostsVm.GenerateTestHostsCommand.CanExecute(null))
            {
                _hostsVm.GenerateTestHostsCommand.Execute(null);
            }
        }
    }
}
