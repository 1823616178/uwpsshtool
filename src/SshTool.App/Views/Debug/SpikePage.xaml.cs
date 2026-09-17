using System.Globalization;
using SshTool.Core.Spikes;
using Windows.ApplicationModel;
using Windows.System.Profile;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Views.Debug
{
    public sealed partial class SpikePage : Page
    {
        public SpikePage()
        {
            this.InitializeComponent();
        }

        private void OnRunClick(object sender, RoutedEventArgs e)
        {
            // 报告第一行自报运行环境：贴出来的结果必须能自证「真机还是 PC」「.NET Native 还是 CoreCLR」，
            // 否则无法判断 Spike 结论是否成立（SP01 已因此返工一次，见 04-TASKS 进度日志 2026-09-18）。
            ReportText.Text = EnvironmentHeader() + "\n" + JsonSpike.RoundTrip();
        }

        private static string EnvironmentHeader()
        {
#if NET_NATIVE
            const string toolchain = ".NET Native";
#else
            const string toolchain = "CoreCLR(IL)";
#endif
#if DEBUG
            const string config = "Debug";
#else
            const string config = "Release";
#endif
            var id = Package.Current.Id;
            var v = id.Version;
            return string.Format(
                CultureInfo.InvariantCulture,
                "ENV: {0} | {1} | {2} {3} | app v{4}.{5}.{6}.{7} | OS {8}",
                AnalyticsInfo.VersionInfo.DeviceFamily,   // Windows.Mobile / Windows.Desktop
                OsVersion(),
                id.Architecture,
                config,
                v.Major, v.Minor, v.Build, v.Revision,
                toolchain);
        }

        // DeviceFamilyVersion 是打包成字符串的 64 位整数，每 16 位一段
        private static string OsVersion()
        {
            ulong raw;
            if (!ulong.TryParse(AnalyticsInfo.VersionInfo.DeviceFamilyVersion,
                    NumberStyles.None, CultureInfo.InvariantCulture, out raw))
            {
                return "unknown";
            }
            return string.Format(
                CultureInfo.InvariantCulture, "{0}.{1}.{2}.{3}",
                (raw & 0xFFFF000000000000UL) >> 48,
                (raw & 0x0000FFFF00000000UL) >> 32,
                (raw & 0x00000000FFFF0000UL) >> 16,
                raw & 0x000000000000FFFFUL);
        }

        private void OnBackClick(object sender, RoutedEventArgs e)
        {
            if (Frame != null && Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }
    }
}
