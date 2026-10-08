using System.Collections.Generic;
using SshTool.App.Dialogs;
using SshTool.App.Platform;
using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Views.Debug
{
    public sealed partial class TokenGalleryPage : Page
    {
        private const string DemoFingerprintOld = "SHA256:ab12cd34ef56gh78ij90kl12mn34op56qr78st90";
        private const string DemoFingerprintNew = "SHA256:zz98yx76wv54ut32sr10qp98on76ml54kj32ih10";
        private const string DemoArt =
            "+--[ED25519 256]--+\n" +
            "|       .o+.      |\n" +
            "|      ..o*       |\n" +
            "|     .. + =      |\n" +
            "|    .  o + .     |\n" +
            "|   . o. S o      |\n" +
            "|  . o.+o .       |\n" +
            "|   ..o*..        |\n" +
            "|   .+++.         |\n" +
            "|  .o=+.          |\n" +
            "+----[SHA256]-----+";

        // V01a 演示区：SegmentTabs 选项与结果格式串（resw，随语言切换）
        private string[] _loginItems;
        private string _segmentFormat;

        public TokenGalleryPage()
        {
            this.InitializeComponent();
            InitV2Demos();
        }

        private void InitV2Demos()
        {
            ResourceLoader loader = ResourceLoader.GetForCurrentView();
            _loginItems = new[]
            {
                loader.GetString("Gallery_SegmentLogin"),
                loader.GetString("Gallery_SegmentRegister")
            };
            DemoSegmentLogin.ItemsSource = _loginItems;
            DemoSegmentLogin.SelectionChanged += OnDemoSegmentChanged;
            DemoSegmentTri.ItemsSource = new[]
            {
                loader.GetString("Gallery_SegmentHosts"),
                loader.GetString("Gallery_SegmentSessions"),
                loader.GetString("Gallery_SegmentTunnels")
            };
            DemoSegmentDisabled.ItemsSource = _loginItems;
            _segmentFormat = loader.GetString("Gallery_SegmentSelected");
            UpdateSegmentResult();

            // 四张 SurfaceCard 的示例正文共用一条 resw（避免同文四键）。
            string cardBody = loader.GetString("Gallery_CardBody/Text");
            CardBody1.Text = cardBody;
            CardBody2.Text = cardBody;
            CardBody3.Text = cardBody;
            CardBody4.Text = cardBody;

            InitV01bDemos(loader);
        }

        // V01b：AppListRow / FormSection / BottomActionBar 演示（状态由 XAML 静态指定，文案在此装配）。
        private void InitV01bDemos(ResourceLoader loader)
        {
            // AppListRow 四态共用示例标题/副标题（同 Gallery_CardBody 先例）；
            // 状态 Pill 文案复用 Gallery_PillSuccess.Text。
            string rowTitle = loader.GetString("Gallery_ListRowTitle");
            string rowSubtitle = loader.GetString("Gallery_ListRowSubtitle");
            DemoRow1.Title = rowTitle;
            DemoRow1.Subtitle = rowSubtitle;
            DemoRow1Status.Text = loader.GetString("Gallery_PillSuccess/Text");
            DemoRow2.Title = rowTitle;
            DemoRow2.Subtitle = rowSubtitle;
            DemoRow3.Title = rowTitle;
            DemoRow3.Subtitle = rowSubtitle;
            DemoRow4.Title = rowTitle;
            DemoRow4.Subtitle = rowSubtitle;

            // FormSection 三态：错误文案直接复用错误码映射键 Validation_PortRange。
            string sectionHeader = loader.GetString("Gallery_FormSectionHeader");
            string sectionDescription = loader.GetString("Gallery_FormSectionDescription");
            string placeholder = loader.GetString("Gallery_FormSectionInput");
            DemoSectionNormal.Header = sectionHeader;
            DemoSectionNormal.Description = sectionDescription;
            DemoSectionError.Header = sectionHeader;
            DemoSectionError.ErrorMessage = loader.GetString("Validation_PortRange");
            DemoSectionReadOnly.Header = sectionHeader;
            DemoSectionReadOnly.Description = sectionDescription;
            DemoSectionInput1.PlaceholderText = placeholder;
            DemoSectionInput2.PlaceholderText = placeholder;
            DemoSectionInput3.PlaceholderText = placeholder;

            // BottomActionBar 两态：normal（含溢出菜单）/ keyboard-visible（XAML 已置 IsKeyboardVisible）。
            string primary = loader.GetString("Gallery_ActionBarPrimary");
            DemoBarNormal.PrimaryText = primary;
            DemoBarKeyboard.PrimaryText = primary;
            DemoBarOverflowItem.Text = loader.GetString("Gallery_ActionBarOverflowItem");
        }

        private void OnBannerRetryClick(object sender, System.EventArgs e)
        {
            BannerRetryResult.Text = ResourceLoader.GetForCurrentView().GetString("Gallery_BannerRetryResult");
        }

        private void OnDemoSegmentChanged(object sender, System.EventArgs e)
        {
            UpdateSegmentResult();
        }

        private void UpdateSegmentResult()
        {
            int index = DemoSegmentLogin.SelectedIndex;
            if (_loginItems == null || index < 0 || index >= _loginItems.Length)
            {
                return;
            }
            SegmentResultText.Text = string.Format(_segmentFormat, _loginItems[index]);
        }

        private void OnThemeDark(object sender, RoutedEventArgs e)
        {
            ThemeService.Apply(AppThemeMode.Dark);
        }

        private void OnThemeLight(object sender, RoutedEventArgs e)
        {
            ThemeService.Apply(AppThemeMode.Light);
        }

        private void OnThemeSystem(object sender, RoutedEventArgs e)
        {
            ThemeService.Apply(AppThemeMode.System);
        }

        private void OnBackClick(object sender, RoutedEventArgs e)
        {
            if (Frame != null && Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }

        private async void OnOverlayDemo(object sender, RoutedEventArgs e)
        {
            DemoOverlay.IsActive = true;
            await System.Threading.Tasks.Task.Delay(2000);
            DemoOverlay.IsActive = false;
        }

        private void OnToastDemo(object sender, RoutedEventArgs e)
        {
            DemoToast.Show("已复制到剪贴板");
        }

        // U05 对话框演示：密码/短语只显示长度，绝不显示明文。

        private async void OnHostKeyDemo(object sender, RoutedEventArgs e)
        {
            var r = await HostKeyDialog.ShowAsync("web-01 (10.0.0.11:22)", "ED25519", DemoFingerprintOld, DemoArt);
            DialogResultText.Text = r.Trusted ? "HostKeyDialog → 信任并连接" : "HostKeyDialog → 已取消";
        }

        private async void OnMismatchDemo(object sender, RoutedEventArgs e)
        {
            var r = await HostKeyMismatchDialog.ShowAsync("web-01", DemoFingerprintOld, DemoFingerprintNew);
            DialogResultText.Text = r.RemoveAndRetry ? "HostKeyMismatchDialog → 移除旧记录并重试" : "HostKeyMismatchDialog → 已取消";
        }

        private async void OnCredentialDemo(object sender, RoutedEventArgs e)
        {
            var r = await CredentialDialog.ShowAsync("web-01");
            DialogResultText.Text = r.Cancelled
                ? "CredentialDialog → 已取消"
                : "CredentialDialog → 连接，密码 ***（长度 " + (r.Password == null ? 0 : r.Password.Length) + "），保存：" + (r.Remember ? "是" : "否");
        }

        private async void OnPassphraseDemo(object sender, RoutedEventArgs e)
        {
            var r = await PassphraseDialog.ShowAsync("lumia-ed25519");
            DialogResultText.Text = r.Cancelled
                ? "PassphraseDialog → 已取消"
                : "PassphraseDialog → 解锁，短语 ***（长度 " + (r.Passphrase == null ? 0 : r.Passphrase.Length) + "），保存：" + (r.Remember ? "是" : "否");
        }

        private async void OnKbdDemo(object sender, RoutedEventArgs e)
        {
            var prompts = new List<KbdInteractivePrompt>
            {
                new KbdInteractivePrompt { Prompt = "Password: ", Echo = false },
                new KbdInteractivePrompt { Prompt = "Verification code: ", Echo = true }
            };
            var r = await KbdInteractiveDialog.ShowAsync("SSH 服务器", "请输入以下凭据完成登录。", prompts);
            if (r.Cancelled)
            {
                DialogResultText.Text = "KbdInteractiveDialog → 已取消";
            }
            else
            {
                var masked = new string[r.Answers.Length];
                for (int i = 0; i < r.Answers.Length; i++)
                {
                    masked[i] = prompts[i].Echo ? r.Answers[i] : "***（长度 " + r.Answers[i].Length + "）";
                }
                DialogResultText.Text = "KbdInteractiveDialog → 提交：" + string.Join(" / ", masked);
            }
        }

        private async void OnConfirmDemo(object sender, RoutedEventArgs e)
        {
            var r = await ConfirmDialog.ShowAsync("删除主机", "删除主机 web-01？相关隧道 2 条、已保存凭据将一并删除。", "删除", "取消", true);
            DialogResultText.Text = r.Confirmed ? "ConfirmDialog → 已确认（危险操作）" : "ConfirmDialog → 已取消";
        }

        private async void OnExitDemo(object sender, RoutedEventArgs e)
        {
            var r = await ExitWithSessionsDialog.ShowAsync(3);
            DialogResultText.Text = r.Exit ? "ExitWithSessionsDialog → 退出" : "ExitWithSessionsDialog → 已取消";
        }

        // 验证 DialogService 排队：两个请求不等待即发，应先后显示且都返回。
        private async void OnQueueDemo(object sender, RoutedEventArgs e)
        {
            var t1 = CredentialDialog.ShowAsync("web-01");
            var t2 = ConfirmDialog.ShowAsync("排队验证", "这是第二个对话框。看到它说明第一个已正常关闭。", "好", "取消");
            var r1 = await t1;
            var r2 = await t2;
            DialogResultText.Text = "连发两个 → 第一个" + (r1.Cancelled ? "取消" : "确认（密码 *** 长度 " + r1.Password.Length + "）")
                + "，第二个" + (r2.Confirmed ? "确认" : "取消");
        }
    }
}
