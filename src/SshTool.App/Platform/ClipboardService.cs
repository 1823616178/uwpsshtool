using System;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace SshTool.App.Platform
{
    // T12：系统剪贴板。不记录文本内容（可能含终端里的口令）。
    // 注意：剪贴板被其他进程占用时 Clipboard.SetContent/GetContent 会抛 COMException
    //（CLIPBRD_E_CANT_OPEN 等）。调用方在 async void / 事件处理器里时一律用 TrySetText，
    // 否则异常直接冒到 Application.UnhandledException 把应用带走。
    public static class ClipboardService
    {
        // 需要自行向用户报告失败的调用方（RecoveryKeyDialog、KeyDetailPage）用这个，会抛异常。
        public static void SetText(string text)
        {
            var package = new DataPackage();
            package.RequestedOperation = DataPackageOperation.Copy;
            package.SetText(text ?? string.Empty);
            Clipboard.SetContent(package);
        }

        // 不抛异常的版本：返回是否写入成功。
        public static bool TrySetText(string text)
        {
            try
            {
                SetText(text);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static async Task<string> GetTextAsync()
        {
            try
            {
                // GetContent 本身也会因剪贴板被占用而抛，必须一并兜住（调用方 PasteClipboard 是 async void）。
                DataPackageView view = Clipboard.GetContent();
                if (view == null || !view.Contains(StandardDataFormats.Text))
                {
                    return string.Empty;
                }
                return await view.GetTextAsync();
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }
}
