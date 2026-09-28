using System;
using SshTool.Core.Storage;
using Xunit;

namespace SshTool.Core.Tests.Storage
{
    // 整型设置的取值范围与枚举类字符串的归一化：范围只在 SettingDefinitions 定义一次，
    // 仓库校验、设置页 ViewModel 钳制与 Slider 范围都从这里取。
    public class SettingDefinitionRangeTests
    {
        [Theory]
        [InlineData("terminalFontSize", 8, 28)]
        [InlineData("scrollbackLines", 1000, 50000)]
        [InlineData("connectTimeoutSeconds", 5, 60)]
        [InlineData("reconnectMaxAttempts", 0, 10)]
        [InlineData("agentKeyTimeoutMinutes", 0, 120)]
        public void RangedInts_DeclareRangeContainingDefault(string key, int min, int max)
        {
            SettingDefinition def = SettingDefinitions.Require(key);
            Assert.Equal(min, def.MinValue);
            Assert.Equal(max, def.MaxValue);
            Assert.True(def.IsValidValue(def.DefaultValue));
        }

        [Fact]
        public void Clamp_PinsToBounds_AndKeepsInRangeValues()
        {
            SettingDefinition def = SettingDefinitions.Require("terminalFontSize");
            Assert.Equal(8, def.Clamp(1));
            Assert.Equal(28, def.Clamp(999));
            Assert.Equal(14, def.Clamp(14));
        }

        [Fact]
        public void Clamp_WithoutRange_ReturnsValueUnchanged()
        {
            SettingDefinition def = SettingDefinitions.Require("backgroundDisconnectMinutes");
            Assert.Null(def.MinValue);
            Assert.Equal(12345, def.Clamp(12345));
        }

        [Fact]
        public void Set_OutOfRange_Throws_AndInRangeWrites()
        {
            var repo = new SettingsRepository(new InMemorySettingsStore());
            Assert.Throws<ArgumentException>(() => repo.TerminalFontSize = 999);
            Assert.Throws<ArgumentException>(() => repo.ConnectTimeoutSeconds = 0);
            repo.TerminalFontSize = 20;
            Assert.Equal(20, repo.TerminalFontSize);
        }

        [Fact]
        public void Read_StoredOutOfRange_FallsBackToDefault()
        {
            var store = new InMemorySettingsStore();
            var repo = new SettingsRepository(store);
            store.Set("scrollbackLines", 10);
            Assert.Equal(5000, repo.ScrollbackLines);
        }

        [Fact]
        public void NormalizeString_KeepsAllowed_FallsBackOtherwise()
        {
            SettingDefinition def = SettingDefinitions.Require("themeMode");
            Assert.Equal("light", def.NormalizeString("light"));
            Assert.Equal("dark", def.NormalizeString("Light"));
            Assert.Equal("dark", def.NormalizeString(null));
            Assert.Equal("dark", def.NormalizeString(string.Empty));
        }

        [Fact]
        public void RangedConstructor_RejectsDefaultOutsideRange()
        {
            Assert.Throws<ArgumentException>(() => new SettingDefinition("x", 50, 0, 10));
            Assert.Throws<ArgumentException>(() => new SettingDefinition("x", 5, 10, 0));
        }
    }
}
