using System.Collections.Generic;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using SshTool.App.Infrastructure;

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
            // M-7：每条许可一行可展开（点标题展开/收起全文）。
            BuildLicenseList(OssPanel, LicenseData.Oss);
            BuildLicenseList(FontsPanel, LicenseData.Fonts);
        }

        private static void BuildLicenseList(StackPanel panel, IReadOnlyList<LicenseEntry> entries)
        {
            panel.Children.Clear();
            foreach (LicenseEntry entry in entries)
            {
                // 标题行：名称 + 许可证类型，点之展开/收起全文。
                var res = Application.Current.Resources;
                var header = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Background = (Brush)res["AppBgBrush"],
                    BorderThickness = (Thickness)res["BorderNone"],
                    Padding = (Thickness)res["PadXs"],
                };
                var headerPanel = new StackPanel();
                headerPanel.Children.Add(new TextBlock
                {
                    Text = entry.Name,
                    Style = (Style)res["BodyTextStyle"],
                });
                headerPanel.Children.Add(new TextBlock
                {
                    Text = entry.LicenseType,
                    Style = (Style)res["CaptionTextStyle"],
                });
                header.Content = headerPanel;

                var detail = new TextBlock
                {
                    Text = entry.FullText,
                    TextWrapping = TextWrapping.Wrap,
                    Style = (Style)res["MonoCaptionTextStyle"],
                    Visibility = Visibility.Collapsed,
                    Margin = (Thickness)res["GapSmBottom"],
                };
                header.Click += (s, e) =>
                {
                    detail.Visibility = detail.Visibility == Visibility.Visible
                        ? Visibility.Collapsed
                        : Visibility.Visible;
                };

                panel.Children.Add(header);
                panel.Children.Add(detail);
            }
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
    }
}
