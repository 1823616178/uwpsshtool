using System;
using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using SshTool.App.Platform;
using SshTool.Core.Keys;
using SshTool.Core.Models;

namespace SshTool.App.ViewModels.Keys
{
    // K02 导入流程 ViewModel（02-UI-DESIGN.md §5.10）：文件/粘贴文本 → 解析
    // （加密时要短语）→ 指纹去重 → 命名保存。对话框负责取文件与输入，
    // 本类持有 KeyImportService 并做状态裁决。短语与私钥不记日志。
    public sealed class KeyImportViewModel : ViewModelBase
    {
        private readonly KeyImportService _service;
        private KeyInspectOutcome _lastOutcome;

        public KeyImportViewModel(AppServices services)
        {
            if (services == null)
            {
                throw new ArgumentNullException("services");
            }
            _service = new KeyImportService(
                services.Keys, services.Secrets, new NativeKeyTool(services.Logger), services.Logger);
        }

        public string InputText { get; set; }

        public string KeyName { get; set; }

        public KeyInspectOutcome LastOutcome
        {
            get { return _lastOutcome; }
        }

        public InspectedKeyInfo LastInspected
        {
            get { return _lastOutcome == null ? null : _lastOutcome.Inspected; }
        }

        public Task<KeyInspectOutcome> InspectAsync(string passphrase)
        {
            return InspectCoreAsync(InputText, passphrase);
        }

        public async Task<KeyEntry> SaveAsync(string passphraseToStore, bool rememberPassphrase)
        {
            if (_lastOutcome == null
                || (_lastOutcome.Status != KeyInspectStatus.Ready
                    && _lastOutcome.Status != KeyInspectStatus.Duplicate))
            {
                throw new InvalidOperationException("解析尚未就绪，不能保存");
            }
            string store = rememberPassphrase ? passphraseToStore : null;
            return await _service.SaveAsync(InputText, KeyName, _lastOutcome.Inspected, store)
                .ConfigureAwait(true);
        }

        // 名称校验：返回 null 表示通过，否则为错误文案。
        public string ValidateName()
        {
            string clean = (KeyName ?? string.Empty).Trim();
            if (clean.Length == 0)
            {
                return Localized.Get("Validation_NameRequired", "名称不能为空");
            }
            if (clean.Length > 255)
            {
                return Localized.Get("Validation_NameTooLong", "名称过长（不超过 255 字）");
            }
            return null;
        }

        // 去重命中时已有密钥的名称（供「已存在」提示）。
        public string DuplicateName()
        {
            if (_lastOutcome == null || _lastOutcome.Existing == null)
            {
                return string.Empty;
            }
            return _lastOutcome.Existing.Name ?? string.Empty;
        }

        private async Task<KeyInspectOutcome> InspectCoreAsync(string text, string passphrase)
        {
            _lastOutcome = await _service.InspectAsync(text, passphrase).ConfigureAwait(true);
            return _lastOutcome;
        }
    }
}
