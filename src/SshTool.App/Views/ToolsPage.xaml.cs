using System;
using SshTool.App.Infrastructure;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Views
{
    // W01（05 §6.1）：「工具与设置」页——次要入口的集中处，纯导航，无 VM。
    public sealed partial class ToolsPage : Page
    {
        public ToolsPage()
        {
            this.InitializeComponent();
        }

        private static NavigationService Nav
        {
            get
            {
                NavigationService nav;
                return ServiceRegistry.TryGet(out nav) ? nav : null;
            }
        }

        private void OnBackRequested(object sender, EventArgs e)
        {
            NavigationService nav = Nav;
            if (nav != null && nav.CanGoBack)
            {
                nav.GoBack();
                return;
            }
            if (Frame != null && Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }

        private void OnSettingsClick(object sender, EventArgs e)
        {
            Nav?.Navigate<SettingsPage>();
        }

        private void OnKeysClick(object sender, EventArgs e)
        {
            Nav?.Navigate<Keys.KeysPage>();
        }

        private void OnSnippetsClick(object sender, EventArgs e)
        {
            Nav?.Navigate<SnippetsPage>(SnippetsArgs.Manage());
        }

        private void OnAppearanceClick(object sender, EventArgs e)
        {
            Nav?.Navigate<AppearanceListPage>();
        }

        private void OnKnownHostsClick(object sender, EventArgs e)
        {
            Nav?.Navigate<KnownHostsPage>();
        }

        private void OnGroupsClick(object sender, EventArgs e)
        {
            Nav?.Navigate<GroupManagePage>();
        }
    }
}
