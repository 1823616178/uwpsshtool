using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using SshTool.App.Dialogs;
using SshTool.App.Infrastructure;
using SshTool.App.Views.Keys;
using SshTool.Core.Common;
using SshTool.Core.Hosts;
using SshTool.Core.Models;
using SshTool.Core.Mvvm;
using SshTool.Core.Storage;

namespace SshTool.App.ViewModels.Keys
{
    public sealed class KeyRow
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Subtitle { get; set; }
        public int UsageCount { get; set; }
        public KeyEntry Source { get; set; }
    }

    // K02 密钥列表（02-UI-DESIGN.md §5.10）。
    // 行副标题：类型 · 指纹尾部 · 被 N 台主机使用。删除保护：被引用时拒绝并列出主机。
    public sealed class KeysViewModel : ViewModelBase
    {
        private readonly AppServices _services;

        public KeysViewModel(AppServices services)
        {
            if (services == null)
            {
                throw new ArgumentNullException("services");
            }
            _services = services;
            Rows = new ObservableCollection<KeyRow>();
            ImportCommand = new AsyncCommand(ImportCoreAsync, onError: OnError);
            GenerateCommand = new AsyncCommand(GenerateCoreAsync, onError: OnError);
            RefreshAsync().Forget("KeysViewModel.Refresh", AppLog.Logger);
        }

        public ObservableCollection<KeyRow> Rows { get; private set; }

        public ICommand ImportCommand { get; private set; }

        public ICommand GenerateCommand { get; private set; }

        public bool IsEmpty
        {
            get { return Rows.Count == 0; }
        }

        public async Task RefreshAsync()
        {
            IReadOnlyList<KeyEntry> keys = await _services.Keys.GetAllAsync().ConfigureAwait(true);
            IReadOnlyList<Host> hosts = await _services.Hosts.GetAllAsync().ConfigureAwait(true);
            Rows.Clear();
            for (int i = 0; i < keys.Count; i++)
            {
                KeyEntry key = keys[i];
                if (key == null)
                {
                    continue;
                }
                Rows.Add(ToRow(key, hosts));
            }
            RaisePropertyChanged("IsEmpty");
        }

        public void OpenDetail(KeyRow row)
        {
            if (row == null)
            {
                return;
            }
            Navigation.Navigate<KeyDetailPage>(KeyDetailArgs.For(row.Id));
        }

        public async Task ImportAsync()
        {
            await ImportCoreAsync().ConfigureAwait(true);
        }

        public async Task GenerateAsync()
        {
            await GenerateCoreAsync().ConfigureAwait(true);
        }

        private async Task ImportCoreAsync()
        {
            KeyImportDialogResult result = await KeyImportDialog.ShowAsync().ConfigureAwait(true);
            if (result == null || result.Cancelled)
            {
                return;
            }
            await RefreshAsync().ConfigureAwait(true);
            // 去重命中时直接看已有密钥，避免用户困惑。
            if (result.Created == null && !string.IsNullOrEmpty(result.ExistingKeyId))
            {
                Navigation.Navigate<KeyDetailPage>(KeyDetailArgs.For(result.ExistingKeyId));
            }
        }

        private async Task GenerateCoreAsync()
        {
            KeyGenerateDialogResult result = await KeyGenerateDialog.ShowAsync().ConfigureAwait(true);
            if (result == null || result.Cancelled || result.Created == null)
            {
                return;
            }
            await RefreshAsync().ConfigureAwait(true);
            Navigation.Navigate<KeyDetailPage>(KeyDetailArgs.For(result.Created.Id));
        }

        public async Task DeleteAsync(KeyRow row)
        {
            if (row == null)
            {
                return;
            }
            IReadOnlyList<Host> refs = await ReferencingHostsAsync(row.Id).ConfigureAwait(true);
            if (refs.Count > 0)
            {
                await ConfirmDialog.ShowAsync(
                    "无法删除",
                    "该密钥正被 " + refs.Count.ToString() + " 台主机使用（"
                        + JoinNames(refs) + "），请先更换这些主机的认证方式。",
                    "确定", "关闭").ConfigureAwait(true);
                return;
            }
            ConfirmDialogResult confirm = await ConfirmDialog.ShowAsync(
                "删除密钥？",
                "删除「" + (row.Name ?? string.Empty) + "」后无法恢复，已保存的私钥与短语将一并清除。",
                "删除", "取消", true).ConfigureAwait(true);
            if (!confirm.Confirmed)
            {
                return;
            }
            try
            {
                await _services.Config.DeleteKeyAsync(row.Id).ConfigureAwait(true);
            }
            catch (InvalidOperationException)
            {
                // 并发：对话框确认期间密钥又被主机引用，ConfigService 拒绝删除。
                IReadOnlyList<Host> raced = await ReferencingHostsAsync(row.Id).ConfigureAwait(true);
                await ConfirmDialog.ShowAsync(
                    "无法删除",
                    "该密钥正被 " + raced.Count.ToString() + " 台主机使用（"
                        + JoinNames(raced) + "），请先更换这些主机的认证方式。",
                    "确定", "关闭").ConfigureAwait(true);
                return;
            }
            Logger.Log(LogLevel.Info, "Keys", "删除密钥 " + row.Id);
            await RefreshAsync().ConfigureAwait(true);
        }

        public async Task<IReadOnlyList<Host>> ReferencingHostsAsync(string keyId)
        {
            IReadOnlyList<Host> hosts = await _services.Hosts.GetAllAsync().ConfigureAwait(true);
            var refs = new List<Host>();
            for (int i = 0; i < hosts.Count; i++)
            {
                if (hosts[i] != null && hosts[i].KeyId == keyId)
                {
                    refs.Add(hosts[i]);
                }
            }
            return refs;
        }

        private static KeyRow ToRow(KeyEntry key, IReadOnlyList<Host> hosts)
        {
            int usage = 0;
            for (int i = 0; i < hosts.Count; i++)
            {
                if (hosts[i] != null && hosts[i].KeyId == key.Id)
                {
                    usage++;
                }
            }
            string subtitle = (key.KeyType ?? string.Empty)
                + " · " + CredentialDraft.FingerprintTail(key.FingerprintSha256);
            if (usage > 0)
            {
                subtitle += " · 被 " + usage.ToString() + " 台主机使用";
            }
            return new KeyRow
            {
                Id = key.Id,
                Name = key.Name ?? string.Empty,
                Subtitle = subtitle,
                UsageCount = usage,
                Source = key
            };
        }

        private static string JoinNames(IReadOnlyList<Host> hosts)
        {
            int take = hosts.Count < 3 ? hosts.Count : 3;
            var names = new List<string>(take);
            for (int i = 0; i < take; i++)
            {
                names.Add(hosts[i].Name ?? string.Empty);
            }
            string text = string.Join("、", names);
            if (hosts.Count > take)
            {
                text += "…";
            }
            return text;
        }

        private void OnError(Exception ex)
        {
            if (ex != null)
            {
                Logger.Log(LogLevel.Error, "Keys", ex.GetType().Name + " " + ex.Message);
            }
        }
    }
}
