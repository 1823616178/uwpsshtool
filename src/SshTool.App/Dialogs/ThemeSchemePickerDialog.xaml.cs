using System.Collections.Generic;
using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using SshTool.Core.Models;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Dialogs
{
    // A04：多 scheme 选择框。返回用户勾选的外观（取消/空选返回空列表）。
    public sealed partial class ThemeSchemePickerDialog : ContentDialog
    {
        private ThemeSchemePickerDialog()
        {
            this.InitializeComponent();
        }

        public static async Task<IReadOnlyList<AppearanceProfile>> ShowAsync(
            IReadOnlyList<AppearanceProfile> schemes)
        {
            var empty = new List<AppearanceProfile>();
            if (schemes == null || schemes.Count == 0)
            {
                return empty;
            }
            var dialog = new ThemeSchemePickerDialog();
            dialog.SummaryText.Text = "文件中找到 " + schemes.Count + " 个配色，选择要导入的：";
            dialog.SchemeList.ItemsSource = schemes;
            dialog.SchemeList.SelectedItems.Add(schemes[0]);
            var result = await ServiceRegistry.Get<DialogService>().ShowAsync(dialog);
            if (result != ContentDialogResult.Primary)
            {
                return empty;
            }
            var selected = new List<AppearanceProfile>();
            for (int i = 0; i < dialog.SchemeList.SelectedItems.Count; i++)
            {
                var profile = dialog.SchemeList.SelectedItems[i] as AppearanceProfile;
                if (profile != null)
                {
                    selected.Add(profile);
                }
            }
            return selected;
        }
    }
}
