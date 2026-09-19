using System;
using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using SshTool.App.Platform;
using SshTool.Core.Keys;
using SshTool.Core.Models;

namespace SshTool.App.ViewModels.Keys
{
    // K02 生成流程 ViewModel（02-UI-DESIGN.md §5.10）：类型（ED25519 推荐 /
    // RSA 3072 / RSA 4096）、名称、注释 → 经 KeyTool 生成 → 落库。
    // 首版生成的私钥未加密（与 native 一致），故无短语项。
    public sealed class KeyGenerateViewModel : ViewModelBase
    {
        private readonly KeyImportService _service;
        private int _typeIndex;
        private bool _isGenerating;
        private string _error;

        public KeyGenerateViewModel(AppServices services)
        {
            if (services == null)
            {
                throw new ArgumentNullException("services");
            }
            _service = new KeyImportService(
                services.Keys, services.Secrets, new NativeKeyTool(services.Logger), services.Logger);
        }

        // 0 = ED25519（推荐）/ 1 = RSA 3072 / 2 = RSA 4096
        public int TypeIndex
        {
            get { return _typeIndex; }
            set { SetProperty(ref _typeIndex, value); }
        }

        public string KeyName { get; set; }

        public string Comment { get; set; }

        public bool IsGenerating
        {
            get { return _isGenerating; }
            private set { SetProperty(ref _isGenerating, value); }
        }

        public string Error
        {
            get { return _error; }
            private set { SetProperty(ref _error, value); }
        }

        public string ValidateName()
        {
            string clean = (KeyName ?? string.Empty).Trim();
            if (clean.Length == 0)
            {
                return "名称不能为空";
            }
            if (clean.Length > 255)
            {
                return "名称不能超过 255 个字符";
            }
            return null;
        }

        public async Task<KeyGenerateOutcome> GenerateAsync()
        {
            string nameError = ValidateName();
            if (nameError != null)
            {
                Error = nameError;
                return new KeyGenerateOutcome { Success = false, Error = nameError };
            }
            KeyGenerateKind kind = KeyGenerateKind.Ed25519;
            int bits = 0;
            if (_typeIndex == 1)
            {
                kind = KeyGenerateKind.Rsa;
                bits = 3072;
            }
            else if (_typeIndex == 2)
            {
                kind = KeyGenerateKind.Rsa;
                bits = 4096;
            }
            IsGenerating = true;
            Error = null;
            try
            {
                // RSA-4096 生成约数秒：native 已在后台线程，此处只等待。
                KeyGenerateOutcome outcome = await _service.GenerateAsync(
                    kind, bits, Comment ?? string.Empty, (KeyName ?? string.Empty).Trim())
                    .ConfigureAwait(true);
                if (!outcome.Success)
                {
                    Error = outcome.Error;
                }
                return outcome;
            }
            finally
            {
                IsGenerating = false;
            }
        }
    }
}
