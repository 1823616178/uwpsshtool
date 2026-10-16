using System;
using System.Globalization;
using Windows.ApplicationModel;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using SshTool.App.Infrastructure;

namespace SshTool.App.Views
{
    public sealed partial class AboutPage : Page
    {
        public AboutPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            VersionText.Text = ReadAppVersion();
            DepsText.Text = ReadDeps();
        }

        private static string ReadAppVersion()
        {
            try
            {
                PackageVersion v = Package.Current.Id.Version;
                return string.Format(CultureInfo.InvariantCulture, "v{0}.{1}.{2}.{3}",
                    v.Major, v.Minor, v.Build, v.Revision);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string ReadDeps()
        {
            string core = "SshTool.Core: " + SshTool.Core.CoreInfo.Version;
            string native_ = "SshTool.Native: " + SshTool.Native.NativeInfo.Version();
            string openssl = "OpenSSL: " + SshTool.Native.NativeInfo.OpenSslVersion();
            string argon2 = "Argon2id: RFC 9106";
            string libssh2 = "libssh2: 1.11.1";
            return string.Join(Environment.NewLine, core, native_, openssl, argon2, libssh2);
        }

        // L-3（02 §4 W06）：页头返回走 NavigationService 统一链（与硬件返回一致）。
        private void OnBackRequested(object sender, EventArgs e)
        {
            NavigationService nav;
            if (ServiceRegistry.TryGet(out nav))
            {
                nav.RequestBack();
            }
            else if (Frame != null && Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }

        private void OnLicensesClick(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(LicensesPage));
        }
    }
}
