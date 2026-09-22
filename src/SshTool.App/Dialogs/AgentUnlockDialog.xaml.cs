using System.Collections.Generic;
using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using SshTool.Core.Sessions;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Dialogs
{
    public sealed partial class AgentUnlockDialog : ContentDialog
    {
        // 对话框行模型：显示串在构造时算好，XAML 不做转换器。
        public sealed class AgentUnlockRow
        {
            public string KeyId { get; set; }
            public string DisplayName { get; set; }
            public string DisplayDetail { get; set; }
        }

        private AgentUnlockDialog()
        {
            this.InitializeComponent();
        }

        // choices 为全部本机密钥（Unlocked 标出已解锁的）；返回所选 KeyId，
        // 取消返回 Cancelled。只回传 id，不接触私钥材料，不记日志。
        public static async Task<AgentUnlockResult> ShowAsync(
            IReadOnlyList<AgentKeyChoice> choices, string hostDisplay)
        {
            var dialog = new AgentUnlockDialog();
            dialog.PromptText.Text = Localized.Format("AgentUnlock_Prompt", "主机 {0}", hostDisplay ?? string.Empty)
                + " 需要密钥认证，解锁一把密钥后继续";
            var rows = new List<AgentUnlockRow>();
            if (choices != null)
            {
                for (int i = 0; i < choices.Count; i++)
                {
                    AgentKeyChoice c = choices[i];
                    if (c == null || string.IsNullOrEmpty(c.KeyId))
                    {
                        continue;
                    }
                    string name = string.IsNullOrEmpty(c.Name) ? c.KeyId : c.Name;
                    string detail = string.IsNullOrEmpty(c.KeyType) ? string.Empty : c.KeyType;
                    string tail = FingerprintTail(c.FingerprintSha256);
                    if (!string.IsNullOrEmpty(tail))
                    {
                        detail = string.IsNullOrEmpty(detail) ? tail : detail + " · " + tail;
                    }
                    detail += c.Unlocked ? " · 已解锁" : " · 未解锁";
                    rows.Add(new AgentUnlockRow
                    {
                        KeyId = c.KeyId,
                        DisplayName = name,
                        DisplayDetail = detail
                    });
                }
            }
            dialog.KeyList.ItemsSource = rows;
            if (rows.Count > 0)
            {
                dialog.KeyList.SelectedIndex = 0;
            }
            dialog.IsPrimaryButtonEnabled = rows.Count > 0;
            var result = await ServiceRegistry.Get<DialogService>().ShowAsync(dialog);
            if (result != ContentDialogResult.Primary)
            {
                return new AgentUnlockResult { Cancelled = true };
            }
            var selected = dialog.KeyList.SelectedItem as AgentUnlockRow;
            if (selected == null)
            {
                return new AgentUnlockResult { Cancelled = true };
            }
            return new AgentUnlockResult { KeyId = selected.KeyId };
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            IsPrimaryButtonEnabled = KeyList.SelectedItem != null;
        }

        private static string FingerprintTail(string fingerprint)
        {
            if (string.IsNullOrEmpty(fingerprint) || fingerprint.Length < 8)
            {
                return fingerprint ?? string.Empty;
            }
            return "…" + fingerprint.Substring(fingerprint.Length - 8);
        }
    }
}
