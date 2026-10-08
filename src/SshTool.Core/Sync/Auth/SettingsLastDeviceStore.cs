using System;
using SshTool.Core.Storage;

namespace SshTool.Core.Sync.Auth
{
    // fix/auth-audit：ILastDeviceStore 落在本机设置（App 侧 ApplicationData.LocalSettings，不漫游）。
    // 键不在 SettingsRepository 的定义表里，因此不会进同步文档、也不会出现在设置页。
    // 值只是 userId + deviceId（非机密）；读写失败一律吞掉（接口约定不抛）。
    public sealed class SettingsLastDeviceStore : ILastDeviceStore
    {
        public const string Key = "Sync.LastDevice";

        private readonly ISettingsStore _settings;

        public SettingsLastDeviceStore(ISettingsStore settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }
            _settings = settings;
        }

        public string Read()
        {
            try
            {
                object value;
                if (_settings.TryGet(Key, out value))
                {
                    string text = value as string;
                    return string.IsNullOrEmpty(text) ? null : text;
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        // 清除写空串：LocalSettings 不保证接受 null 值。
        public void Write(string value)
        {
            try
            {
                _settings.Set(Key, value ?? string.Empty);
            }
            catch (Exception)
            {
            }
        }
    }
}
