using System.Text;
using SshTool.Core.Terminal;
using Xunit;

namespace SshTool.Core.Tests.Terminal
{
    // T09：01-DESIGN.md §7.5 哨兵差分（新增 / 删除 / 替换 / 多字符 / 整段删除后恢复）。
    public class SentinelDiffTests
    {
        [Fact]
        public void Sentinel_IsTwoZeroWidthSpaces()
        {
            Assert.Equal("\u200B\u200B", SentinelDiff.Sentinel);
            Assert.Equal(2, SentinelDiff.Sentinel.Length);
        }

        [Fact]
        public void Compute_UnchangedSentinel_IsEmpty()
        {
            SentinelDiffResult diff = SentinelDiff.Compute(SentinelDiff.Sentinel);

            Assert.True(diff.IsEmpty);
            Assert.Equal(string.Empty, diff.Inserted);
            Assert.Equal(0, diff.Deleted);
            Assert.False(diff.ShouldSendDeletes);
        }

        [Fact]
        public void Compute_NullOrEmpty_TreatsAsWholeDelete()
        {
            SentinelDiffResult fromNull = SentinelDiff.Compute(null);
            SentinelDiffResult fromEmpty = SentinelDiff.Compute(string.Empty);

            Assert.Equal(2, fromNull.Deleted);
            Assert.Equal(2, fromEmpty.Deleted);
            Assert.Equal(string.Empty, fromNull.Inserted);
            Assert.True(fromEmpty.ShouldSendDeletes);
        }

        [Fact]
        public void Compute_InsertAtEnd_ReportsAddedText()
        {
            SentinelDiffResult diff = SentinelDiff.Compute(SentinelDiff.Sentinel + "hello");

            Assert.Equal("hello", diff.Inserted);
            Assert.Equal(0, diff.Deleted);
            Assert.False(diff.ShouldSendDeletes);
            Assert.Equal(Encoding.UTF8.GetBytes("hello"), diff.InsertedUtf8());
        }

        [Fact]
        public void Compute_MultiCharacterCommit_KeepsWholeString()
        {
            // SP05：连打可合并成 "la"；输入法一次提交「击查看」或 emoji 代理对。
            SentinelDiffResult latin = SentinelDiff.Compute(SentinelDiff.Sentinel + "la");
            SentinelDiffResult phrase = SentinelDiff.Compute(SentinelDiff.Sentinel + "击查看");
            SentinelDiffResult emoji = SentinelDiff.Compute(SentinelDiff.Sentinel + "😀");

            Assert.Equal("la", latin.Inserted);
            Assert.Equal("击查看", phrase.Inserted);
            Assert.Equal("😀", emoji.Inserted);
            Assert.Equal(0, latin.Deleted);
            Assert.Equal(Encoding.UTF8.GetBytes("你好"),
                SentinelDiff.Compute(SentinelDiff.Sentinel + "你好").InsertedUtf8());
        }

        [Fact]
        public void Compute_DeleteOneSentinelChar_ReportsOneDelete()
        {
            SentinelDiffResult diff = SentinelDiff.Compute("\u200B");

            Assert.Equal(string.Empty, diff.Inserted);
            Assert.Equal(1, diff.Deleted);
            Assert.True(diff.ShouldSendDeletes);
        }

        [Fact]
        public void Compute_Replace_SentinelSwappedForCommittedText()
        {
            // IME 把整框换成上屏词：有新增就不把哨兵缺失当远端退格。
            SentinelDiffResult diff = SentinelDiff.Compute("你好");

            Assert.Equal("你好", diff.Inserted);
            Assert.Equal(2, diff.Deleted);
            Assert.False(diff.ShouldSendDeletes);
            Assert.Equal(new byte[] { 0xE4, 0xBD, 0xA0, 0xE5, 0xA5, 0xBD }, diff.InsertedUtf8());
        }

        [Fact]
        public void Compute_InsertWithOneSentinelEaten_StillPrefersInsertedText()
        {
            SentinelDiffResult diff = SentinelDiff.Compute("\u200B" + "nihao");

            Assert.Equal("nihao", diff.Inserted);
            Assert.Equal(1, diff.Deleted);
            Assert.False(diff.ShouldSendDeletes);
        }

        [Fact]
        public void WholeSentinelDeleted_ThenRestore_IsClean()
        {
            SentinelDiffResult deleted = SentinelDiff.Compute(string.Empty);
            Assert.Equal(2, deleted.Deleted);
            Assert.True(deleted.ShouldSendDeletes);

            string restored = SentinelDiff.Restore();
            Assert.True(SentinelDiff.IsRestored(restored));
            Assert.True(SentinelDiff.Compute(restored).IsEmpty);
        }

        [Fact]
        public void ExtraSentinelChars_DoNotCountAsInsertOrDelete()
        {
            SentinelDiffResult diff = SentinelDiff.Compute("\u200B\u200B\u200B");

            Assert.True(diff.IsEmpty);
            Assert.False(SentinelDiff.IsRestored("\u200B\u200B\u200B"));
        }

        [Fact]
        public void TerminalInputEventArgs_RejectsNull()
        {
            Assert.Throws<System.ArgumentNullException>(() => new TerminalInputEventArgs(null));
            var args = new TerminalInputEventArgs(new byte[] { 0x0D });
            Assert.Equal(new byte[] { 0x0D }, args.Data);
        }
    }
}
