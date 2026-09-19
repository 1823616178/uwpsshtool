using System.Collections.Generic;
using System.Text;
using SshTool.Core.Terminal;
using Xunit;

namespace SshTool.Core.Tests.Terminal
{
    public class PasteProcessorTests
    {
        [Fact]
        public void NormalizeNewlines_MixesCrLfLfCr()
        {
            Assert.Equal("a\rb\rc\rd", PasteProcessor.NormalizeNewlines("a\r\nb\nc\rd"));
            Assert.Equal("a\rb\rc\r", PasteProcessor.NormalizeNewlines("a\r\nb\nc\r"));
            Assert.Equal("a\rb", PasteProcessor.NormalizeNewlines("a\r\nb"));
            Assert.Equal("x\ry", PasteProcessor.NormalizeNewlines("x\ry"));
        }

        [Fact]
        public void LineCount_CountsNormalizedRows()
        {
            Assert.Equal(0, PasteProcessor.LineCount(""));
            Assert.Equal(1, PasteProcessor.LineCount("hello"));
            Assert.Equal(2, PasteProcessor.LineCount("a\rb"));
            Assert.Equal(3, PasteProcessor.LineCount("a\rb\rc"));
        }

        [Fact]
        public void NeedsMultilineConfirm_RequiresSettingAndTwoLines()
        {
            Assert.False(PasteProcessor.NeedsMultilineConfirm("one", true));
            Assert.True(PasteProcessor.NeedsMultilineConfirm("a\rb", true));
            Assert.False(PasteProcessor.NeedsMultilineConfirm("a\rb", false));
        }

        [Fact]
        public void WrapBracketed_AddsCsiMarkers()
        {
            Assert.Equal("hi", PasteProcessor.WrapBracketed("hi", false));
            Assert.Equal("\u001b[200~hi\u001b[201~", PasteProcessor.WrapBracketed("hi", true));
        }

        [Fact]
        public void ChunkUtf8_DoesNotSplitMultibyteCharacter()
        {
            string text = "你好";
            IReadOnlyList<byte[]> chunks = PasteProcessor.ChunkUtf8(text, 4);
            Assert.Equal(2, chunks.Count);
            Assert.Equal(Encoding.UTF8.GetBytes("你"), chunks[0]);
            Assert.Equal(Encoding.UTF8.GetBytes("好"), chunks[1]);
            var joined = new byte[chunks[0].Length + chunks[1].Length];
            System.Array.Copy(chunks[0], 0, joined, 0, chunks[0].Length);
            System.Array.Copy(chunks[1], 0, joined, chunks[0].Length, chunks[1].Length);
            Assert.Equal(text, Encoding.UTF8.GetString(joined));
        }

        [Fact]
        public void ChunkUtf8_ExactBoundaryAscii()
        {
            IReadOnlyList<byte[]> chunks = PasteProcessor.ChunkUtf8("abcd", 2);
            Assert.Equal(2, chunks.Count);
            Assert.Equal(new byte[] { (byte)'a', (byte)'b' }, chunks[0]);
            Assert.Equal(new byte[] { (byte)'c', (byte)'d' }, chunks[1]);
        }

        [Fact]
        public void Prepare_NormalizesWrapsAndChunks()
        {
            bool confirm;
            IReadOnlyList<byte[]> chunks = PasteProcessor.Prepare("a\r\nb", true, true, out confirm);
            Assert.True(confirm);
            string all = Encoding.UTF8.GetString(Join(chunks));
            Assert.StartsWith("\u001b[200~", all);
            Assert.EndsWith("\u001b[201~", all);
            Assert.Contains("a\rb", all);
        }

        private static byte[] Join(IReadOnlyList<byte[]> chunks)
        {
            int n = 0;
            for (int i = 0; i < chunks.Count; i++)
            {
                n += chunks[i].Length;
            }
            var all = new byte[n];
            int o = 0;
            for (int i = 0; i < chunks.Count; i++)
            {
                System.Array.Copy(chunks[i], 0, all, o, chunks[i].Length);
                o += chunks[i].Length;
            }
            return all;
        }
    }
}
