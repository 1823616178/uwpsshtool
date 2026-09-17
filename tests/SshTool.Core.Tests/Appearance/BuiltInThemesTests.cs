using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SshTool.Core.Appearance;
using SshTool.Core.Models;
using SshTool.Core.Storage;
using SshTool.Core.Storage.Repositories;
using Xunit;

namespace SshTool.Core.Tests.Appearance
{
    // A02 验收：9 套、每套 20 个色位合法 #RRGGBB、id 唯一。
    public class BuiltInThemesTests
    {
        private static readonly Regex ColorRegex = new Regex("^#[0-9A-Fa-f]{6}$");

        [Fact]
        public void NineThemes_UniqueIds_AllBuiltinSlug()
        {
            var all = BuiltInThemes.All;
            Assert.Equal(9, all.Count);
            Assert.Equal(9, all.Select(t => t.Id).Distinct().Count());
            foreach (var t in all)
            {
                Assert.StartsWith("builtin-", t.Id);
                Assert.True(t.BuiltIn, t.Id);
                Assert.False(string.IsNullOrWhiteSpace(t.Name));
            }
            Assert.Contains(all, t => t.Id == AppearanceResolver.DefaultBuiltInId);
        }

        [Fact]
        public void ExpectedSlugs_AllPresent()
        {
            var ids = BuiltInThemes.All.Select(t => t.Id).ToList();
            var expected = new[]
            {
                "builtin-harmony-dark", "builtin-harmony-light", "builtin-one-dark",
                "builtin-dracula", "builtin-nord", "builtin-solarized-dark",
                "builtin-solarized-light", "builtin-tokyo-night", "builtin-github-light"
            };
            foreach (var id in expected)
            {
                Assert.Contains(id, ids);
            }
        }

        [Fact]
        public void EveryTheme_20ValidColorSlots()
        {
            foreach (var t in BuiltInThemes.All)
            {
                Assert.Equal(16, t.Palette.Count);
                var slots = t.Palette.Concat(new[] { t.Foreground, t.Background, t.Cursor, t.Selection });
                Assert.Equal(20, slots.Count());
                foreach (var c in slots)
                {
                    Assert.Matches(ColorRegex, c);
                }
            }
        }

        [Fact]
        public void EveryTheme_PropertiesInDesignRanges()
        {
            foreach (var t in BuiltInThemes.All)
            {
                Assert.InRange(t.FontSize, 8, 28);
                Assert.InRange(t.LineHeight, 1.0, 1.6);
                Assert.InRange(t.Padding, 0, 16);
                Assert.False(string.IsNullOrWhiteSpace(t.FontFamily));
            }
        }

        [Fact]
        public void All_ReturnsClones_NotSharedInstances()
        {
            var a = BuiltInThemes.All;
            var b = BuiltInThemes.All;
            Assert.NotSame(a[0], b[0]);
            a[0].Palette[0] = "#000000";
            Assert.NotEqual(a[0].Palette[0], b[0].Palette[0]);
        }

        [Fact]
        public async Task Service_WithBuiltInThemes_ResolvesSettingsDefault()
        {
            // A01+A02 接线：D04 默认 defaultAppearanceId = builtin-harmony-dark 必须命中。
            var fs = new InMemoryFileSystem();
            var service = new AppearanceService(
                new AppearanceRepository(fs),
                new HostRepository(fs),
                new SettingsRepository(new InMemorySettingsStore()),
                BuiltInThemes.All);
            var r = await service.ResolveAsync(null);
            Assert.Equal(AppearanceSource.GlobalDefault, r.Source);
            Assert.Equal(AppearanceResolver.DefaultBuiltInId, r.Profile.Id);
            Assert.True(service.IsBuiltIn(AppearanceResolver.DefaultBuiltInId));
        }
    }
}
