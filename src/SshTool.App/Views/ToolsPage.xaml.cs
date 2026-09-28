using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SshTool.App.Dialogs;
using SshTool.App.Infrastructure;
using SshTool.Core.Common;
using SshTool.Core.Hosts;
using SshTool.Core.Models;
using Windows.Storage;
using Windows.Storage.Pickers;
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

        private void OnImportConfigClick(object sender, EventArgs e)
        {
            ImportConfigAsync().Forget("ToolsPage.ImportConfig", AppLog.Logger);
        }

        // W05：选文件 → 解析 → 汇总确认 → 批量添加。私钥不随导入（只提示需要自行导入的别名数）。
        private static async Task ImportConfigAsync()
        {
            AppServices services = AppServices.Current;
            if (services == null)
            {
                return;
            }
            var picker = new FileOpenPicker { ViewMode = PickerViewMode.List };
            picker.FileTypeFilter.Add("*");
            StorageFile file = await picker.PickSingleFileAsync();
            if (file == null)
            {
                return;
            }
            string text;
            try
            {
                text = await FileIO.ReadTextAsync(file);
            }
            catch (Exception ex)
            {
                AppLog.Error("ToolsPage", "read ssh_config failed", ex);
                await ConfirmDialog.ShowAsync(
                    Localized.Get("Tools_ImportTitle", "导入 ssh_config"),
                    Localized.Get("Tools_ImportReadFailed", "无法读取该文件，请确认它是 UTF-8 文本。"),
                    Localized.Get("Dialog_Ok", "确定"), null);
                return;
            }
            IReadOnlyList<Host> existing = await services.Hosts.GetAllAsync();
            var names = new List<string>();
            foreach (Host host in existing)
            {
                names.Add(host.Name);
            }
            SshConfigImportResult result = SshConfigImporter.ToHosts(SshConfigImporter.Parse(text), names);
            int hostCount = result.Hosts.Count;
            int duplicates = result.SkippedDuplicates.Count;
            int incomplete = result.SkippedIncomplete.Count;
            int patterns = result.SkippedPatterns.Count;
            string message = Localized.Format("Tools_ImportSummary", "将导入 {0} 台主机。跳过：重名 {1}、缺少 User {2}、通配符模式 {3}。", hostCount, duplicates, incomplete, patterns);
            if (result.NeedsKey.Count > 0)
            {
                string keyNote = Localized.Format("Tools_ImportNeedsKey", "其中 {0} 台配置了 IdentityFile：私钥不会导入，请在「密钥」中导入后到主机编辑页关联。", result.NeedsKey.Count);
                message += "\n" + keyNote;
            }
            if (result.Hosts.Count == 0)
            {
                await ConfirmDialog.ShowAsync(Localized.Get("Tools_ImportTitle", "导入 ssh_config"), message,
                    Localized.Get("Dialog_Ok", "确定"), null);
                return;
            }
            ConfirmDialogResult confirm = await ConfirmDialog.ShowAsync(
                Localized.Get("Tools_ImportTitle", "导入 ssh_config"), message,
                Localized.Get("Tools_ImportConfirm", "导入"), Localized.Get("Dialog_Cancel", "取消"));
            if (confirm.Confirmed)
            {
                await services.Hosts.AddManyAsync(result.Hosts);
            }
        }
    }
}
