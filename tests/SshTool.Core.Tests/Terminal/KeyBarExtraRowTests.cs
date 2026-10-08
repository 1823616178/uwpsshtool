using System.Collections.Generic;
using SshTool.Core.Terminal;
using Xunit;

namespace SshTool.Core.Tests.Terminal
{
    public class KeyBarExtraRowTests
    {
        [Fact]
        public void ExtraRowDefault_AllIdsKnown()
        {
            string[] ids = KeyBarLayout.ExtraRowDefaultString.Split(',');
            foreach (string id in ids)
            {
                KeyBarKey key;
                Assert.True(KeyBarLayout.TryGet(id, out key), id);
            }
        }

        [Fact]
        public void DefaultMain_ExtraRowHasNoOverlap()
        {
            var main = new HashSet<string>();
            foreach (KeyBarKey key in KeyBarLayout.Parse(KeyBarLayout.DefaultString))
            {
                main.Add(key.Id);
            }
            IReadOnlyList<KeyBarKey> extra = KeyBarLayout.ExtraRowFor(KeyBarLayout.DefaultString);
            Assert.NotEmpty(extra);
            foreach (KeyBarKey key in extra)
            {
                Assert.DoesNotContain(key.Id, main);
            }
            Assert.Equal(KeyBarLayout.ExtraRowDefaultString.Split(',').Length, extra.Count);
        }

        [Fact]
        public void KeysAlreadyInMain_AreRemovedFromExtraRow()
        {
            IReadOnlyList<KeyBarKey> extra = KeyBarLayout.ExtraRowFor("esc,shift,underscore,del");
            foreach (KeyBarKey key in extra)
            {
                Assert.NotEqual("shift", key.Id);
                Assert.NotEqual("underscore", key.Id);
                Assert.NotEqual("del", key.Id);
            }
            Assert.Equal("colon", extra[0].Id);
        }

        [Fact]
        public void EmptyMain_UsesDefaultMainForDedupe()
        {
            Assert.Equal(
                KeyBarLayout.ExtraRowFor(KeyBarLayout.DefaultString).Count,
                KeyBarLayout.ExtraRowFor(string.Empty).Count);
        }

        [Fact]
        public void MainContainsEverything_ExtraRowEmpty()
        {
            Assert.Empty(KeyBarLayout.ExtraRowFor(KeyBarLayout.ExtraRowDefaultString));
        }
    }
}
