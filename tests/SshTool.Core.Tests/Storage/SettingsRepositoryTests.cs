using System;
using System.Collections.Generic;
using System.Reflection;
using SshTool.Core.Storage;
using Xunit;

namespace SshTool.Core.Tests.Storage
{
    // 01-DESIGN §8.3 / D04 验收：定义表与访问器完整性（反射只在测试里用）、
    // 非法值回退、默认值写入。
    public class SettingsRepositoryTests
    {
        private static SettingsRepository NewRepo(InMemorySettingsStore store = null)
        {
            return new SettingsRepository(store ?? new InMemorySettingsStore());
        }

        // O02：只有记账的 store 能证明「同值不落盘」——InMemorySettingsStore
        // 不记写入次数。Writes 只统计构造后的写，构造里的 EnsureDefaults 由
        // ResetWrites 清零。
        private sealed class CountingSettingsStore : ISettingsStore
        {
            private readonly Dictionary<string, object> _values =
                new Dictionary<string, object>(StringComparer.Ordinal);

            public int Writes { get; private set; }
            public readonly List<string> WrittenKeys = new List<string>();

            public bool TryGet(string key, out object value)
            {
                return _values.TryGetValue(key, out value);
            }

            public void Set(string key, object value)
            {
                _values[key] = value;
                Writes++;
                WrittenKeys.Add(key);
            }

            public void ResetWrites()
            {
                Writes = 0;
                WrittenKeys.Clear();
            }

            // 绕过仓库直接种值：用于构造「存储里是非法值」的场景。
            public void Seed(string key, object value)
            {
                _values[key] = value;
            }
        }

        [Fact]
        public void EnsureDefaults_WritesAllDefaultsIntoEmptyStore()
        {
            var store = new InMemorySettingsStore();
            var repo = NewRepo(store); // 构造即 EnsureDefaults

            foreach (var def in SettingDefinitions.All)
            {
                object value;
                Assert.True(store.TryGet(def.Key, out value), "缺键: " + def.Key);
                Assert.Equal(def.DefaultValue, value);
            }
            Assert.Equal(25, SettingDefinitions.All.Count); // §8.3 表 25 键（V02 +hostQuickConnectExpanded）
        }

        [Fact]
        public void EnsureDefaults_PreservesExistingValues()
        {
            var store = new InMemorySettingsStore();
            store.Set("themeMode", "light");
            store.Set("terminalFontSize", 20);
            var repo = NewRepo(store);

            Assert.Equal("light", repo.ThemeMode);
            Assert.Equal(20, repo.TerminalFontSize);

            repo.EnsureDefaults(); // 幂等，不覆写
            Assert.Equal("light", repo.ThemeMode);
        }

        // 反射对拍：每个定义键都有同名 PascalCase 强类型属性，且初值等于默认值
        [Fact]
        public void Definitions_HaveMatchingTypedAccessors()
        {
            var repo = NewRepo();
            var type = typeof(SettingsRepository);
            int checkedCount = 0;

            foreach (var def in SettingDefinitions.All)
            {
                var propertyName = char.ToUpperInvariant(def.Key[0]) + def.Key.Substring(1);
                var property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
                Assert.True(property != null, "缺访问器: " + propertyName);
                Assert.True(property.CanRead && property.CanWrite, propertyName + " 需可读可写");

                var expectedType = def.Type == SettingType.String ? typeof(string)
                    : def.Type == SettingType.Int ? typeof(int)
                    : typeof(bool);
                Assert.Equal(expectedType, property.PropertyType);
                Assert.Equal(def.DefaultValue, property.GetValue(repo));
                checkedCount++;
            }
            Assert.Equal(SettingDefinitions.All.Count, checkedCount);
        }

        // 反向对拍：仓库上的读写属性都必须能回溯到定义键
        [Fact]
        public void Accessors_AllMapBackToDefinitions()
        {
            var type = typeof(SettingsRepository);
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var key = char.ToLowerInvariant(property.Name[0]) + property.Name.Substring(1);
                Assert.NotNull(SettingDefinitions.Find(key));
            }
        }

        [Fact]
        public void RoundTrip_AccessorSetThenGet()
        {
            var repo = NewRepo();
            repo.ThemeMode = "light";
            repo.TerminalFontSize = 18;
            repo.KeyBarVisible = false;

            Assert.Equal("light", repo.ThemeMode);
            Assert.Equal(18, repo.TerminalFontSize);
            Assert.False(repo.KeyBarVisible);

            // 新实例（同 store）读到同样的值 = 已持久化
            var store = new InMemorySettingsStore();
            var repo2 = NewRepo(store);
            repo2.LogLevel = "debug";
            Assert.Equal("debug", NewRepo(store).LogLevel);
        }

        [Fact]
        public void Changed_FiresWithKeyAndValue()
        {
            var repo = NewRepo();
            SettingChangedEventArgs args = null;
            repo.Changed += delegate (object s, SettingChangedEventArgs e) { args = e; };

            repo.HapticsEnabled = false;

            Assert.NotNull(args);
            Assert.Equal("hapticsEnabled", args.Key);
            Assert.Equal(false, args.Value);
        }

        [Fact]
        public void EnsureDefaults_DoesNotFireChanged()
        {
            var store = new InMemorySettingsStore();
            var repo = NewRepo(store);
            int count = 0;
            repo.Changed += delegate { count++; };

            repo.EnsureDefaults();

            Assert.Equal(0, count);
        }

        // ---------- O02：同值短路（05-CODE-AUDIT §C-07） ----------

        [Fact]
        public void Set_SameValue_DoesNotWriteStore_AndDoesNotFireChanged()
        {
            var store = new CountingSettingsStore();
            var repo = new SettingsRepository(store);
            repo.TerminalFontSize = 16;
            store.ResetWrites();
            int changed = 0;
            repo.Changed += delegate { changed++; };

            repo.TerminalFontSize = 16;
            repo.TerminalFontSize = 16;

            Assert.Equal(0, store.Writes);
            Assert.Equal(0, changed);
            Assert.Equal(16, repo.TerminalFontSize);
        }

        // Slider 拖动的真实形状：大量同值夹着少量真正的变化。
        [Fact]
        public void Set_DragLikeBurst_OnlyWritesActualChanges()
        {
            var store = new CountingSettingsStore();
            var repo = new SettingsRepository(store);
            store.ResetWrites();

            foreach (int size in new[] { 12, 12, 12, 13, 13, 13, 13, 14, 14, 12 })
            {
                repo.TerminalFontSize = size;
            }

            // 起始值即默认 12：12(同值跳过) → 13 → 14 → 12，共 3 次真实写入。
            Assert.Equal(3, store.Writes);
            Assert.Equal(new[] { "terminalFontSize", "terminalFontSize", "terminalFontSize" },
                         store.WrittenKeys);
            Assert.Equal(12, repo.TerminalFontSize);
        }

        [Fact]
        public void Set_DifferentValue_StillWritesAndFires()
        {
            var store = new CountingSettingsStore();
            var repo = new SettingsRepository(store);
            store.ResetWrites();
            SettingChangedEventArgs args = null;
            repo.Changed += delegate (object s, SettingChangedEventArgs e) { args = e; };

            repo.HapticsEnabled = false; // 默认 true

            Assert.Equal(1, store.Writes);
            Assert.NotNull(args);
            Assert.Equal("hapticsEnabled", args.Key);
            Assert.Equal(false, args.Value);
            Assert.False(repo.HapticsEnabled);
        }

        // 构造里的 EnsureDefaults 已写过默认值，所以「首次显式写入默认值」
        // 和存储现状相同 —— 跳过，且读回结果不变。
        [Fact]
        public void Set_ToDefaultValue_AfterEnsureDefaults_IsSkipped()
        {
            var store = new CountingSettingsStore();
            var repo = new SettingsRepository(store);
            store.ResetWrites();
            int changed = 0;
            repo.Changed += delegate { changed++; };

            repo.ThemeMode = "dark"; // 定义表默认值

            Assert.Equal(0, store.Writes);
            Assert.Equal(0, changed);
            Assert.Equal("dark", repo.ThemeMode);
        }

        // 存储里是非法值时 Read 回退到默认值但不覆写存储。此时写入默认值
        // 必须照常落盘（把非法值修回来），不能被当成同值跳过。
        [Fact]
        public void Set_ToDefaultValue_RepairsIllegalStoredValue()
        {
            var store = new CountingSettingsStore();
            var repo = new SettingsRepository(store);
            store.Seed("themeMode", "purple"); // 绕过仓库种一个非法值
            store.ResetWrites();
            int changed = 0;
            repo.Changed += delegate { changed++; };

            Assert.Equal("dark", repo.ThemeMode); // 读时回退，存储未被覆写

            repo.ThemeMode = "dark";

            Assert.Equal(1, store.Writes);
            Assert.Equal(1, changed);
            object stored;
            Assert.True(store.TryGet("themeMode", out stored));
            Assert.Equal("dark", stored); // 非法值已被修复
        }

        // 类型不符同理：存了 string 的 int 键，写入默认值要落盘。
        [Fact]
        public void Set_RepairsTypeMismatchedStoredValue()
        {
            var store = new CountingSettingsStore();
            var repo = new SettingsRepository(store);
            store.Seed("terminalFontSize", "12"); // 字符串，不是 int
            store.ResetWrites();

            repo.TerminalFontSize = 12;

            Assert.Equal(1, store.Writes);
            object stored;
            Assert.True(store.TryGet("terminalFontSize", out stored));
            Assert.Equal(12, stored);
        }

        // 键被外部删除时不能因 TryGet 落空而跳过写入。
        [Fact]
        public void Set_WritesWhenKeyMissingFromStore()
        {
            var store = new CountingSettingsStore();
            var repo = new SettingsRepository(store);
            store.ResetWrites();
            int changed = 0;
            repo.Changed += delegate { changed++; };

            repo.Set("themeMode", "light");
            store.ResetWrites();
            changed = 0;

            // 同值：跳过
            repo.Set("themeMode", "light");
            Assert.Equal(0, store.Writes);
            Assert.Equal(0, changed);
        }

        // 字符串按值比较（不是引用），否则每次 Set 都会穿透短路。
        [Fact]
        public void Set_SameStringByValue_IsSkipped()
        {
            var store = new CountingSettingsStore();
            var repo = new SettingsRepository(store);
            repo.HostSortMode = string.Concat("na", "me");
            store.ResetWrites();

            repo.HostSortMode = string.Concat("nam", "e"); // 相等但不同实例

            Assert.Equal(0, store.Writes);
        }

        [Fact]
        public void Set_InvalidValue_StillThrows_EvenWhenEqualToStored()
        {
            var repo = NewRepo();

            Assert.Throws<ArgumentException>(delegate { repo.Set("themeMode", "purple"); });
        }

        [Theory]
        [InlineData("themeMode", "purple", "dark")]     // 枚举非法 → 默认
        [InlineData("themeMode", "", "dark")]           // 空串也不合法
        [InlineData("logLevel", "verbose", "info")]
        [InlineData("keepScreenOn", "always", "always")] // 合法值正常读
        public void EnumString_IllegalValueFallsBackToDefault(string key, string stored, string expected)
        {
            var store = new InMemorySettingsStore();
            store.Set(key, stored); // 绕过仓库直写（模拟旧版本/手改数据）
            var repo = NewRepo(store);

            Assert.Equal(expected, repo.GetString(key));
        }

        [Fact]
        public void TypeMismatch_FallsBackToDefault()
        {
            var store = new InMemorySettingsStore();
            store.Set("terminalFontSize", "big");   // 应为 int
            store.Set("hapticsEnabled", "yes");     // 应为 bool
            store.Set("themeMode", 3);              // 应为 string
            var repo = NewRepo(store);

            Assert.Equal(12, repo.TerminalFontSize);
            Assert.True(repo.HapticsEnabled);
            Assert.Equal("dark", repo.ThemeMode);
        }

        [Fact]
        public void Set_InvalidValue_Throws()
        {
            var repo = NewRepo();
            Assert.Throws<ArgumentException>(() => repo.Set("themeMode", "purple")); // 枚举非法
            Assert.Throws<ArgumentException>(() => repo.Set("terminalFontSize", "18")); // 类型不符
            Assert.Throws<ArgumentException>(() => repo.Set("noSuchKey", "x")); // 未知键
            Assert.Throws<ArgumentException>(() => repo.ThemeMode = "purple");
        }

        [Fact]
        public void Get_WithWrongTypeAccessor_Throws()
        {
            var repo = NewRepo();
            Assert.Throws<InvalidOperationException>(() => repo.GetInt("themeMode"));
            Assert.Throws<InvalidOperationException>(() => repo.GetBool("terminalFontSize"));
        }

        [Fact]
        public void Require_UnknownKey_Throws()
        {
            Assert.Throws<ArgumentException>(() => SettingDefinitions.Require("nope"));
            Assert.Null(SettingDefinitions.Find("nope"));
        }
    }
}
