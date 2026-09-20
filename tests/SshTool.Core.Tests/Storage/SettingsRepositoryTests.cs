using System;
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
