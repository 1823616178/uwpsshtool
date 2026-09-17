using SshTool.Core.Spikes;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Views.Debug
{
    public sealed partial class SpikePage : Page
    {
        private string _lastReport;

        public SpikePage()
        {
            this.InitializeComponent();
        }

        private async void OnRunClick(object sender, RoutedEventArgs e)
        {
            // 报告首行自报运行环境：贴出来的结果必须能自证「真机还是 PC」「.NET Native 还是 CoreCLR」，
            // 否则无法判断 Spike 结论是否成立（SP01 已因此返工一次，见 04-TASKS 进度日志 2026-09-18）。
            _lastReport = DebugReport.EnvironmentHeader() + "\n" + JsonSpike.RoundTrip();
            ReportText.Text = _lastReport;
            CopyButton.IsEnabled = true;
            // 落盘 + 复制到剪贴板 + 写 app.log，省得人对着屏幕抄
            StatusText.Text = await DebugReport.PublishAsync("sp01-json", "SP01", _lastReport);
        }

        private void OnCopyClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_lastReport)) { return; }
            StatusText.Text = DebugReport.CopyToClipboard(_lastReport) ? "已复制到剪贴板" : "复制失败";
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
