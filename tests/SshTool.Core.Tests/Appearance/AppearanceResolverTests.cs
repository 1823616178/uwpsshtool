using System;
using System.Collections.Generic;
using SshTool.Core.Appearance;
using SshTool.Core.Models;
using Xunit;

namespace SshTool.Core.Tests.Appearance
{
    // A01 验收：解析优先级。
    public class AppearanceResolverTests
    {
        internal static AppearanceProfile Profile(string id, bool builtIn = false)
        {
            var palette = new List<string>();
            for (int i = 0; i < 16; i++)
            {
                palette.Add("#112233");
            }
            return new AppearanceProfile
            {
                Id = id,
                Name = id,
                BuiltIn = builtIn,
                FontFamily = "Consolas",
                FontSize = 12,
                LineHeight = 1.2,
                CursorStyle = CursorStyle.Block,
                CursorBlink = true,
                Padding = 4,
                Palette = palette,
                Foreground = "#EEEEEE",
                Background = "#111111",
                Cursor = "#EEEEEE",
                Selection = "#334455"
            };
        }

        internal static readonly IReadOnlyList<AppearanceProfile> TwoBuiltIns = new List<AppearanceProfile>
        {
            Profile(AppearanceResolver.DefaultBuiltInId, builtIn: true),
            Profile("builtin-other", builtIn: true)
        };

        [Fact]
        public void HostAppearanceId_Wins()
        {
            var profiles = new List<AppearanceProfile> { Profile("user-a"), Profile("user-default") };
            var r = AppearanceResolver.Resolve("user-a", "user-default", profiles, TwoBuiltIns);
            Assert.Equal(AppearanceSource.Host, r.Source);
            Assert.Equal("user-a", r.Profile.Id);
        }

        [Fact]
        public void NullHostAppearanceId_FallsToGlobalDefault()
        {
            var profiles = new List<AppearanceProfile> { Profile("user-default") };
            var r = AppearanceResolver.Resolve(null, "user-default", profiles, TwoBuiltIns);
            Assert.Equal(AppearanceSource.GlobalDefault, r.Source);
            Assert.Equal("user-default", r.Profile.Id);
        }

        [Fact]
        public void DanglingHostAppearanceId_FallsToGlobalDefault()
        {
            var profiles = new List<AppearanceProfile> { Profile("user-default") };
            var r = AppearanceResolver.Resolve("deleted-id", "user-default", profiles, TwoBuiltIns);
            Assert.Equal(AppearanceSource.GlobalDefault, r.Source);
            Assert.Equal("user-default", r.Profile.Id);
        }

        [Fact]
        public void DanglingDefault_FallsToBuiltIn()
        {
            var r = AppearanceResolver.Resolve(null, "missing-default", new List<AppearanceProfile>(), TwoBuiltIns);
            Assert.Equal(AppearanceSource.BuiltInFallback, r.Source);
            Assert.Equal(AppearanceResolver.DefaultBuiltInId, r.Profile.Id);
        }

        [Fact]
        public void BuiltInFallback_PrefersHarmonyDark_ThenFirst()
        {
            var noHarmony = new List<AppearanceProfile> { Profile("builtin-x", builtIn: true) };
            var r = AppearanceResolver.Resolve(null, null, new List<AppearanceProfile>(), noHarmony);
            Assert.Equal(AppearanceSource.BuiltInFallback, r.Source);
            Assert.Equal("builtin-x", r.Profile.Id);
        }

        [Fact]
        public void EmptyBuiltIns_Throws()
        {
            Assert.Throws<InvalidOperationException>(() =>
                AppearanceResolver.Resolve(null, null, new List<AppearanceProfile>(), new List<AppearanceProfile>()));
        }

        [Fact]
        public void Default_CanPointToBuiltIn()
        {
            var r = AppearanceResolver.Resolve(null, "builtin-other", new List<AppearanceProfile>(), TwoBuiltIns);
            Assert.Equal(AppearanceSource.GlobalDefault, r.Source);
            Assert.Equal("builtin-other", r.Profile.Id);
        }
    }
}
