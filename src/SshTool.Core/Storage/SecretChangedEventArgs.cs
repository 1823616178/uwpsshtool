using System;
using System.Collections.Generic;

namespace SshTool.Core.Storage
{
    // 主机凭据变更事件参数（S14 同步触发器用）。
    // ChangedKeys 只携带键名（如 "host:<id>:password"），绝不携带值；
    // 键名中的主机 id 是本地 UUID，不属于敏感材料，可进日志。
    public sealed class SecretChangedEventArgs : EventArgs
    {
        public SecretChangedEventArgs(ChangeOrigin origin, IReadOnlyList<string> changedKeys)
        {
            Origin = origin;
            ChangedKeys = changedKeys ?? new string[0];
        }

        public ChangeOrigin Origin { get; private set; }

        public IReadOnlyList<string> ChangedKeys { get; private set; }

        // 同步相关的凭据键：host: 前缀（主机密码/短语）与 key: 前缀（私钥原文/短语）
        // 会影响上行文档的 secrets 段（见 SyncLocalAdapter.BuildServerAsync）。
        // 其他前缀（若未来出现）一律视为与同步无关。
        public static bool IsCredentialKey(string key)
        {
            return !string.IsNullOrEmpty(key)
                && (key.StartsWith("host:", StringComparison.Ordinal)
                    || key.StartsWith("key:", StringComparison.Ordinal));
        }

        public static bool HasCredentialKey(IReadOnlyList<string> keys)
        {
            if (keys == null)
            {
                return false;
            }
            for (int i = 0; i < keys.Count; i++)
            {
                if (IsCredentialKey(keys[i]))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
