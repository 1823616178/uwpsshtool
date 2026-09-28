using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Tasks;
using SshTool.App.Dialogs;
using SshTool.App.Infrastructure;
using SshTool.App.Platform;
using SshTool.Core.Common;
using SshTool.Core.Hosts;
using SshTool.Core.Models;
using SshTool.Core.Storage;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Provider;

namespace SshTool.App.ViewModels.Keys
{
    public sealed class KeyHostRow
    {
        public string HostId { get; set; }
        public string Name { get; set; }
        public string Subtitle { get; set; }
    }

    // K02 密钥详情（02-UI-DESIGN.md §5.10）：改名、复制/分享公钥、导出私钥
    // （二次确认）、使用它的主机、删除保护。
    public sealed class KeyDetailViewModel : ViewModelBase
    {
        private readonly AppServices _services;
        private KeyEntry _entry;
        private string _name;

        public KeyDetailViewModel(AppServices services)
        {
            if (services == null)
            {
                throw new ArgumentNullException("services");
            }
            _services = services;
            Hosts = new ObservableCollection<KeyHostRow>();
        }

        public ObservableCollection<KeyHostRow> Hosts { get; private set; }

        public string KeyId
        {
            get { return _entry == null ? string.Empty : _entry.Id; }
        }

        public string Name
        {
            get { return _name ?? string.Empty; }
            set
            {
                if (SetProperty(ref _name, value ?? string.Empty))
                {
                    RaisePropertyChanged("CanRename");
                }
            }
        }

        public bool CanRename
        {
            get
            {
                return _entry != null
                    && !string.Equals(_name ?? string.Empty, _entry.Name ?? string.Empty, StringComparison.Ordinal);
            }
        }

        public string KeyTypeText
        {
            get { return _entry == null ? string.Empty : _entry.KeyType ?? string.Empty; }
        }

        public string BitsText
        {
            get
            {
                if (_entry == null)
                {
                    return string.Empty;
                }
                return _entry.Bits.ToString(CultureInfo.InvariantCulture);
            }
        }

        public string FormatText
        {
            get { return _entry == null ? string.Empty : _entry.Format ?? string.Empty; }
        }

        public string EncryptedText
        {
            get
            {
                if (_entry == null)
                {
                    return string.Empty;
                }
                return _entry.Encrypted
                    ? Localized.Get("Common_Yes", "是")
                    : Localized.Get("Common_No", "否");
            }
        }

        public string Fingerprint
        {
            get { return _entry == null ? string.Empty : _entry.FingerprintSha256 ?? string.Empty; }
        }

        public string PublicKey
        {
            get { return _entry == null ? string.Empty : _entry.PublicKeyOpenSsh ?? string.Empty; }
        }

        public string CreatedAt
        {
            get { return _entry == null ? string.Empty : _entry.CreatedAt ?? string.Empty; }
        }

        public bool HasHosts
        {
            get { return Hosts.Count > 0; }
        }

        // 返回 false 表示密钥不存在（已被删除），页面应直接返回。
        public async Task<bool> LoadAsync(string keyId)
        {
            _entry = await _services.Keys.GetByIdAsync(keyId).ConfigureAwait(true);
            if (_entry == null)
            {
                return false;
            }
            _name = _entry.Name ?? string.Empty;
            await RefreshHostsAsync().ConfigureAwait(true);
            RaiseAll();
            return true;
        }

        public async Task<string> RenameAsync()
        {
            if (_entry == null)
            {
                return Localized.Get("Key_NotFound", "密钥不存在");
            }
            string clean = (_name ?? string.Empty).Trim();
            if (clean.Length == 0)
            {
                return Localized.Get("Validation_NameRequired", "名称不能为空");
            }
            if (clean.Length > 255)
            {
                return Localized.Get("Validation_NameTooLong", "名称过长（不超过 255 字）");
            }
            _entry.Name = clean;
            await _services.Keys.UpdateAsync(_entry, ChangeOrigin.User).ConfigureAwait(true);
            Logger.Log(LogLevel.Info, "Keys", "重命名密钥 " + _entry.Id);
            RaisePropertyChanged("CanRename");
            return null;
        }

        // 删除保护：被主机引用时拒绝并列出主机；成功返回 true（页面返回）。
        public async Task<bool> DeleteAsync()
        {
            if (_entry == null)
            {
                return false;
            }
            await RefreshHostsAsync().ConfigureAwait(true);
            if (Hosts.Count > 0)
            {
                await ShowBlockedDialogAsync();
                return false;
            }
            ConfirmDialogResult confirm = await ConfirmDialog.ShowAsync(
                Localized.Get("Key_DeleteConfirmTitle", "删除密钥？"),
                Localized.Format("Key_DeleteConfirmMessage", "删除「{0}」后无法恢复，已保存的私钥与短语将一并清除。",
                    _entry.Name ?? string.Empty),
                Localized.Get("Common_Delete", "删除"),
                Localized.Get("Dialog_Cancel", "取消"), true).ConfigureAwait(true);
            if (!confirm.Confirmed)
            {
                return false;
            }
            try
            {
                await _services.Config.DeleteKeyAsync(_entry.Id).ConfigureAwait(true);
            }
            catch (InvalidOperationException)
            {
                await RefreshHostsAsync().ConfigureAwait(true);
                await ShowBlockedDialogAsync();
                return false;
            }
            Logger.Log(LogLevel.Info, "Keys", "删除密钥 " + _entry.Id);
            return true;
        }

        // 「密钥仍被主机引用」的提示对话框（DeleteAsync 两处共用）。
        private async Task ShowBlockedDialogAsync()
        {
            await ConfirmDialog.ShowAsync(
                Localized.Get("Key_DeleteBlockedTitle", "无法删除"),
                Localized.Format("Key_DeleteBlockedMessage", "该密钥正被 {0} 台主机使用（{1}），请先更换这些主机的认证方式。",
                    Hosts.Count.ToString(), JoinNames()),
                Localized.Get("Dialog_Ok", "确定"),
                Localized.Get("Common_Close", "关闭")).ConfigureAwait(true);
        }

        public void CopyPublicKey()
        {
            if (_entry == null || string.IsNullOrEmpty(_entry.PublicKeyOpenSsh))
            {
                return;
            }
            ClipboardService.SetText(_entry.PublicKeyOpenSsh);
            bool haptics;
            try
            {
                haptics = _services.Settings.GetBool("hapticsEnabled");
            }
            catch (Exception)
            {
                haptics = true;
            }
            Haptics.VibrateLight(haptics);
            // 只记动作与密钥 id，不记公钥内容。
            Logger.Log(LogLevel.Info, "Keys", "复制公钥 " + _entry.Id);
        }

        // 导出私钥（二次确认 + FileSavePicker 落盘）。返回给页面显示的一句话。
        // 私钥内容只在内存与目标文件之间流转，不记日志。
        public async Task<string> ExportPrivateAsync()
        {
            if (_entry == null)
            {
                return Localized.Get("Key_NotFound", "密钥不存在");
            }
            ConfirmDialogResult first = await ConfirmDialog.ShowAsync(
                Localized.Get("Key_ExportConfirmTitle", "导出私钥？"),
                Localized.Get("Key_ExportConfirmMessage", "私钥将以明文写入你选择的文件，任何能读到该文件的应用都能使用它。"),
                Localized.Get("Common_Continue", "继续"),
                Localized.Get("Dialog_Cancel", "取消"), true).ConfigureAwait(true);
            if (!first.Confirmed)
            {
                return Localized.Get("Export_Cancelled", "已取消导出");
            }
            ConfirmDialogResult second = await ConfirmDialog.ShowAsync(
                Localized.Get("Key_ExportAgainTitle", "再次确认"),
                Localized.Get("Key_ExportAgainMessage", "私钥一旦离开应用即失去 DPAPI 保护。请确认目标位置只有你能访问。"),
                Localized.Get("Common_Export", "导出"),
                Localized.Get("Dialog_Cancel", "取消"), true).ConfigureAwait(true);
            if (!second.Confirmed)
            {
                return Localized.Get("Export_Cancelled", "已取消导出");
            }
            string privateText = await _services.Secrets.GetAsync(SecretKeys.KeyPrivate(_entry.Id))
                .ConfigureAwait(true);
            if (string.IsNullOrEmpty(privateText))
            {
                return Localized.Get("Key_PrivateMissing", "私钥不存在（可能已被清除），无法导出");
            }
            FileSavePicker picker = new FileSavePicker();
            picker.SuggestedFileName = SuggestFileName(_entry.Name);
            picker.FileTypeChoices.Add(Localized.Get("Key_PrivatePemType", "私钥文件"), new List<string> { ".pem" });
            StorageFile target;
            try
            {
                target = await picker.PickSaveFileAsync();
            }
            catch (Exception ex)
            {
                LogFailure("打开保存对话框失败", ex);
                return Localized.Get("Export_OpenLocationFailed", "无法打开保存位置，请重试或换个文件夹");
            }
            if (target == null)
            {
                return Localized.Get("Export_Cancelled", "已取消导出");
            }
            try
            {
                CachedFileManager.DeferUpdates(target);
                await FileIO.WriteTextAsync(target, privateText);
                FileUpdateStatus status = await CachedFileManager.CompleteUpdatesAsync(target);
                if (status != FileUpdateStatus.Complete)
                {
                    LogFailure("私钥导出未确认完成 status=" + status.ToString(), null);
                    return Localized.Get("Export_WriteFailed", "未能写入所选位置，请关闭占用该文件的程序后重试");
                }
            }
            catch (Exception ex)
            {
                LogFailure("导出私钥失败", ex);
                return Localized.Get("Export_Failed", "导出失败，请重试");
            }
            finally
            {
                privateText = null;
            }
            Logger.Log(LogLevel.Info, "Keys", "导出私钥 " + _entry.Id);
            return Localized.Get("Key_Exported", "已导出私钥");
        }

        public string GetHostId(KeyHostRow row)
        {
            return row == null ? null : row.HostId;
        }

        private async Task RefreshHostsAsync()
        {
            Hosts.Clear();
            if (_entry == null)
            {
                RaisePropertyChanged("HasHosts");
                return;
            }
            IReadOnlyList<Host> hosts = await _services.Hosts.GetAllAsync().ConfigureAwait(true);
            for (int i = 0; i < hosts.Count; i++)
            {
                Host h = hosts[i];
                if (h == null || h.KeyId != _entry.Id)
                {
                    continue;
                }
                Hosts.Add(new KeyHostRow
                {
                    HostId = h.Id,
                    Name = h.Name ?? string.Empty,
                    Subtitle = (h.Username ?? string.Empty) + "@" + (h.HostName ?? string.Empty)
                });
            }
            RaisePropertyChanged("HasHosts");
        }

        private string JoinNames()
        {
            int take = Hosts.Count < 3 ? Hosts.Count : 3;
            var names = new List<string>(take);
            for (int i = 0; i < take; i++)
            {
                names.Add(Hosts[i].Name);
            }
            string text = string.Join("、", names);
            if (Hosts.Count > take)
            {
                text += "…";
            }
            return text;
        }

        private static string SuggestFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "key";
            }
            char[] invalid = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };
            string clean = name.Trim();
            for (int i = 0; i < invalid.Length; i++)
            {
                clean = clean.Replace(invalid[i].ToString(), "_");
            }
            return clean.Length == 0 ? "key" : clean;
        }

        // 异常细节只进日志（写盘前由 FileLogger 过 Redactor；私钥内容从不入日志），
        // 上屏一律是一句能看懂的中文短句。ex 允许为 null。
        private void LogFailure(string what, Exception ex)
        {
            try
            {
                Logger.Log(LogLevel.Error, "Keys", what + "："
                    + (ex == null ? "无异常对象" : ex.GetType().Name + " " + ex.Message));
            }
            catch (Exception)
            {
            }
        }

        private void RaiseAll()
        {
            RaisePropertyChanged("KeyId");
            RaisePropertyChanged("Name");
            RaisePropertyChanged("CanRename");
            RaisePropertyChanged("KeyTypeText");
            RaisePropertyChanged("BitsText");
            RaisePropertyChanged("FormatText");
            RaisePropertyChanged("EncryptedText");
            RaisePropertyChanged("Fingerprint");
            RaisePropertyChanged("PublicKey");
            RaisePropertyChanged("CreatedAt");
        }
    }
}
