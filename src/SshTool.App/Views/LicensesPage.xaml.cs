using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace SshTool.App.Views
{
    public sealed partial class LicensesPage : Page
    {
        public LicensesPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            OssText.Text = ReadOssLicenses();
            FontsText.Text = ReadFontLicenses();
        }

        private static string ReadOssLicenses()
        {
            return string.Join(Environment.NewLine,
                "OpenSSL 3.x — Apache License 2.0",
                "libssh2 1.11.1 — BSD 3-Clause",
                "Argon2 (phc-winner-argon2) — CC0 1.0 / Apache 2.0",
                "libvterm 0.3.3 — MIT License",
                "Newtonsoft.Json 12.0.3 — MIT License",
                "Win2D.uwp — MIT License");
        }

        private static string ReadFontLicenses()
        {
            return string.Join(Environment.NewLine,
                "JetBrains Mono — SIL Open Font License 1.1",
                "MDL2 Assets (Segoe MDL2) — Microsoft Software License Terms");
        }

        private void OnBackRequested(object sender, EventArgs e)
        {
            Frame root = Window.Current.Content as Frame;
            if (root != null && root.CanGoBack)
            {
                root.GoBack();
            }
        }
    }
}
