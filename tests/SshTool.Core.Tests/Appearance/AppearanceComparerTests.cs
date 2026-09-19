using SshTool.Core.Appearance;
using SshTool.Core.Models;
using Xunit;

namespace SshTool.Core.Tests.Appearance
{
    // A03：草稿脏检查。
    public class AppearanceComparerTests
    {
        [Fact]
        public void Clone_IsEqual()
        {
            AppearanceProfile a = SampleScreenBuilderTests.Theme();
            Assert.True(AppearanceComparer.AreEqual(a, a.Clone()));
        }

        [Fact]
        public void NameChange_IsDirty()
        {
            AppearanceProfile a = SampleScreenBuilderTests.Theme();
            AppearanceProfile b = a.Clone();
            b.Name = b.Name + " x";
            Assert.False(AppearanceComparer.AreEqual(a, b));
        }

        [Fact]
        public void PaletteChange_IsDirty()
        {
            AppearanceProfile a = SampleScreenBuilderTests.Theme();
            AppearanceProfile b = a.Clone();
            b.Palette[0] = "#FFFFFF";
            Assert.False(AppearanceComparer.AreEqual(a, b));
        }

        [Fact]
        public void ColorCaseInsensitive_StillEqual()
        {
            AppearanceProfile a = SampleScreenBuilderTests.Theme();
            AppearanceProfile b = a.Clone();
            b.Foreground = b.Foreground.ToLowerInvariant();
            Assert.True(AppearanceComparer.AreEqual(a, b));
        }

        [Fact]
        public void NullHandling()
        {
            AppearanceProfile a = SampleScreenBuilderTests.Theme();
            Assert.False(AppearanceComparer.AreEqual(a, null));
            Assert.False(AppearanceComparer.AreEqual(null, a));
            Assert.True(AppearanceComparer.AreEqual(null, null));
        }
    }
}
