using System;
using SshTool.Core.Storage;

namespace SshTool.Core.Sync
{
    // feat/remember-vault：本机同步选项（App 侧 ApplicationData.LocalSettings，不漫游）。
    // 键不在 SettingsRepository 的定义表里，因此不会进同步文档。读写失败一律吞掉，读失败回默认值。
    public sealed class SyncDeviceOptions
    {
        public const string RememberVaultKeyKey = "Sync.RememberVaultKey";

        // 默认开：同一账号在本机退出后重新登录、登录过期后重新登录都不必再输入同步密码。
        public const bool DefaultRememberVaultKey = true;

        private readonly ISettingsStore _settings;

        public SyncDeviceOptions(ISettingsStore settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }
            _settings = settings;
        }

        public bool RememberVaultKey
        {
            get
            {
                try
                {
                    object value;
                    if (_settings.TryGet(RememberVaultKeyKey, out value) && value is bool)
                    {
                        return (bool)value;
                    }
                }
                catch (Exception)
                {
                }
                return DefaultRememberVaultKey;
            }
            set
            {
                try
                {
                    _settings.Set(RememberVaultKeyKey, value);
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
