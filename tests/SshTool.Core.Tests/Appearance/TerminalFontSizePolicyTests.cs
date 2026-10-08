using System.Threading.Tasks;
using SshTool.Core.Appearance;
using SshTool.Core.Common;
using SshTool.Core.Models;
using SshTool.Core.Storage;
using SshTool.Core.Storage.Repositories;
using Xunit;

namespace SshTool.Core.Tests.Appearance
{
    // fix/functional-pass（P1-4）：字号来源/持久化与界面语言映射。
    public class TerminalFontSizePolicyTests
    {
        private sealed class Fixture
        {
            public readonly InMemoryFileSystem Fs = new InMemoryFileSystem();
            public readonly HostRepository Hosts;
            public readonly AppearanceRepository Appearances;
            public readonly SettingsRepository Settings;
            public readonly AppearanceService Service;

            public Fixture()
            {
                Hosts = new HostRepository(Fs);
                Appearances = new AppearanceRepository(Fs);
                Settings = new SettingsRepository(new InMemorySettingsStore());
                Settings.EnsureDefaults();
                Service = new AppearanceService(Appearances, Hosts, Settings, AppearanceResolverTests.TwoBuiltIns);
            }
        }

        [Fact]
        public void Effective_BuiltInUsesSetting_CustomUsesProfile()
        {
            var p = new AppearanceProfile { FontSize = 20 };
            Assert.Equal(15, TerminalFontSizePolicy.Effective(p, true, 15));
            Assert.Equal(20, TerminalFontSizePolicy.Effective(p, false, 15));
            Assert.Equal(15, TerminalFontSizePolicy.Effective(new AppearanceProfile { FontSize = 0 }, false, 15));
            Assert.Equal(TerminalFontSizePolicy.DefaultSize, TerminalFontSizePolicy.Effective(p, true, 0));
            Assert.Equal(28, TerminalFontSizePolicy.Effective(p, true, 99));
            Assert.Equal(8, TerminalFontSizePolicy.Effective(new AppearanceProfile { FontSize = 3 }, false, 15));
        }

        [Fact]
        public async Task Resolve_BuiltIn_TakesSettingWithoutMutatingSharedProfile()
        {
            var f = new Fixture();
            f.Settings.TerminalFontSize = 18;
            int before = AppearanceResolverTests.TwoBuiltIns[0].FontSize;

            AppearanceProfile p = await TerminalFontSizePolicy.ResolveAsync(f.Service, f.Settings, null);

            Assert.Equal(18, p.FontSize);
            Assert.Equal(before, AppearanceResolverTests.TwoBuiltIns[0].FontSize);
        }

        [Fact]
        public async Task PlanPersist_BuiltIn_TargetsSetting()
        {
            var f = new Fixture();
            TerminalFontSizePolicy.PersistPlan plan = await TerminalFontSizePolicy.PlanPersistAsync(f.Service, null, 40);
            Assert.Null(plan.AppearanceToUpdate);
            Assert.Equal(28, plan.Size);
        }

        [Fact]
        public async Task PlanPersist_Custom_UpdatesCopyOfAppearance()
        {
            var f = new Fixture();
            AppearanceProfile custom = AppearanceResolverTests.Profile("user-a");
            custom.FontSize = 12;
            await f.Appearances.AddAsync(custom);
            var host = new Host { Id = "h1", Name = "h1", HostName = "example.com", Username = "u", AppearanceId = "user-a" };
            await f.Hosts.AddAsync(host);

            TerminalFontSizePolicy.PersistPlan plan = await TerminalFontSizePolicy.PlanPersistAsync(f.Service, host, 16);

            Assert.NotNull(plan.AppearanceToUpdate);
            Assert.Equal("user-a", plan.AppearanceToUpdate.Id);
            Assert.Equal(16, plan.AppearanceToUpdate.FontSize);
            Assert.Equal(12, (await f.Appearances.GetByIdAsync("user-a")).FontSize);

            TerminalFontSizePolicy.PersistPlan same = await TerminalFontSizePolicy.PlanPersistAsync(f.Service, host, 12);
            Assert.True(same.NoChange);
        }

        [Theory]
        [InlineData("system", "")]
        [InlineData(null, "")]
        [InlineData("zh-CN", "zh-CN")]
        [InlineData("en-us", "en-US")]
        [InlineData("fr-FR", "")]
        public void Language_MapsToOverride(string setting, string expected)
        {
            Assert.Equal(expected, LanguagePolicy.ToPrimaryLanguageOverride(setting));
        }
    }
}
