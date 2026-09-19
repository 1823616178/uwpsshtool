using System;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace SshTool.App.Platform
{
    // T12：系统剪贴板。不记录文本内容（可能含终端里的口令）。
    public static class ClipboardService
    {
        public static void SetText(string text)
        {
            var package = new DataPackage();
            package.RequestedOperation = DataPackageOperation.Copy;
            package.SetText(text ?? string.Empty);
            Clipboard.SetContent(package);
        }

        public static async Task<string> GetTextAsync()
        {
            DataPackageView view = Clipboard.GetContent();
            if (view == null || !view.Contains(StandardDataFormats.Text))
            {
                return string.Empty;
            }
            try
            {
                return await view.GetTextAsync();
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }
}
