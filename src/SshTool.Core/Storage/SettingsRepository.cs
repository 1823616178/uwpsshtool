using System;

namespace SshTool.Core.Storage
{
    public sealed class SettingChangedEventArgs : EventArgs
    {
        public SettingChangedEventArgs(string key, object value)
        {
            Key = key;
            Value = value;
        }

        public string Key { get; private set; }
        public object Value { get; private set; }
    }

    // 01-DESIGN §8.3 设置仓库：定义表驱动；读时类型不符/枚举非法回退默认；
    // 写时校验（非法值是编程错误，抛 ArgumentException）；Set 触发 Changed。
    public sealed class SettingsRepository
    {
        private readonly ISettingsStore _store;

        public SettingsRepository(ISettingsStore store)
        {
            if (store == null)
            {
                throw new ArgumentNullException(nameof(store));
            }
            _store = store;
            EnsureDefaults();
        }

        public event EventHandler<SettingChangedEventArgs> Changed;

        // 缺失键写入默认值；已有值（包括非法值——读时回退，但不覆写存储）不碰。不触发 Changed。
        public void EnsureDefaults()
        {
            foreach (var def in SettingDefinitions.All)
            {
                object ignored;
                if (!_store.TryGet(def.Key, out ignored))
                {
                    _store.Set(def.Key, def.DefaultValue);
                }
            }
        }

        public string GetString(string key)
        {
            return (string)Read(RequireType(key, SettingType.String));
        }

        public int GetInt(string key)
        {
            return (int)Read(RequireType(key, SettingType.Int));
        }

        public bool GetBool(string key)
        {
            return (bool)Read(RequireType(key, SettingType.Bool));
        }

        // O02：同值写入直接返回——不落盘、不广播。Slider 拖动一次会发上百个
        // ValueChanged，每个都写 LocalSettings 并扇出到外观重算/同步脏标记，
        // 在 ARM32 上是可感知的卡顿（05-CODE-AUDIT §C-07）。
        //
        // 短路的判定对象是**存储里的原始值**，不是 Read() 的有效值：存的是非法值
        // 时（旧版本遗留、外部写坏）Read 会回退到默认值但不覆写存储，此时若拿
        // 有效值比较，`Set(key, 默认值)` 会被误判为同值而跳过，非法值就永远留在
        // 存储里。所以只有「存的值合法且相等」才跳过，否则照常写入——顺带把非法
        // 值修回来，与改动前的行为一致。
        //
        // 构造函数的 EnsureDefaults 已为缺失键写入默认值，因此正常路径上
        // TryGet 必然命中；未命中（键被外部删除）时照常写入。
        public void Set(string key, object value)
        {
            var def = SettingDefinitions.Require(key);
            if (!def.IsValidValue(value))
            {
                throw new ArgumentException("设置值非法: " + key, nameof(value));
            }
            object current;
            if (_store.TryGet(key, out current) && def.IsValidValue(current) && Equals(current, value))
            {
                return;
            }
            _store.Set(key, value);
            var handler = Changed;
            if (handler != null)
            {
                handler(this, new SettingChangedEventArgs(key, value));
            }
        }

        private object Read(SettingDefinition def)
        {
            object value;
            if (!_store.TryGet(def.Key, out value) || !def.IsValidValue(value))
            {
                return def.DefaultValue;
            }
            return value;
        }

        private static SettingDefinition RequireType(string key, SettingType type)
        {
            var def = SettingDefinitions.Require(key);
            if (def.Type != type)
            {
                throw new InvalidOperationException("设置键 " + key + " 的类型是 " + def.Type + "，不是 " + type);
            }
            return def;
        }

        // ---------- 强类型访问器（与 SettingDefinitions.All 逐键对应） ----------

        public string ThemeMode { get { return GetString("themeMode"); } set { Set("themeMode", value); } }
        public string DefaultAppearanceId { get { return GetString("defaultAppearanceId"); } set { Set("defaultAppearanceId", value); } }
        public int TerminalFontSize { get { return GetInt("terminalFontSize"); } set { Set("terminalFontSize", value); } }
        public string KeepScreenOn { get { return GetString("keepScreenOn"); } set { Set("keepScreenOn", value); } }
        public bool KeepAliveInBackground { get { return GetBool("keepAliveInBackground"); } set { Set("keepAliveInBackground", value); } }
        public int BackgroundDisconnectMinutes { get { return GetInt("backgroundDisconnectMinutes"); } set { Set("backgroundDisconnectMinutes", value); } }
        public bool HapticsEnabled { get { return GetBool("hapticsEnabled"); } set { Set("hapticsEnabled", value); } }
        public string KeyBarLayout { get { return GetString("keyBarLayout"); } set { Set("keyBarLayout", value); } }
        public bool KeyBarVisible { get { return GetBool("keyBarVisible"); } set { Set("keyBarVisible", value); } }
        public bool PasteConfirmMultiline { get { return GetBool("pasteConfirmMultiline"); } set { Set("pasteConfirmMultiline", value); } }
        public string AltScreenScroll { get { return GetString("altScreenScroll"); } set { Set("altScreenScroll", value); } }
        public int ScrollbackLines { get { return GetInt("scrollbackLines"); } set { Set("scrollbackLines", value); } }
        public string Shortcuts { get { return GetString("shortcuts"); } set { Set("shortcuts", value); } }
        public string HostGroupCollapsed { get { return GetString("hostGroupCollapsed"); } set { Set("hostGroupCollapsed", value); } }
        public string HostSortMode { get { return GetString("hostSortMode"); } set { Set("hostSortMode", value); } }
        public bool UseSystemAccent { get { return GetBool("useSystemAccent"); } set { Set("useSystemAccent", value); } }
        public bool ShowQuickConnect { get { return GetBool("showQuickConnect"); } set { Set("showQuickConnect", value); } }
        public bool HostQuickConnectExpanded { get { return GetBool("hostQuickConnectExpanded"); } set { Set("hostQuickConnectExpanded", value); } }
        public string Language { get { return GetString("language"); } set { Set("language", value); } }
        public int ConnectTimeoutSeconds { get { return GetInt("connectTimeoutSeconds"); } set { Set("connectTimeoutSeconds", value); } }
        public int ReconnectMaxAttempts { get { return GetInt("reconnectMaxAttempts"); } set { Set("reconnectMaxAttempts", value); } }
        public int AgentKeyTimeoutMinutes { get { return GetInt("agentKeyTimeoutMinutes"); } set { Set("agentKeyTimeoutMinutes", value); } }
        public int SyncPollForegroundSeconds { get { return GetInt("syncPollForegroundSeconds"); } set { Set("syncPollForegroundSeconds", value); } }
        public string LastVersionSeen { get { return GetString("lastVersionSeen"); } set { Set("lastVersionSeen", value); } }
        public string LogLevel { get { return GetString("logLevel"); } set { Set("logLevel", value); } }
    }
}
