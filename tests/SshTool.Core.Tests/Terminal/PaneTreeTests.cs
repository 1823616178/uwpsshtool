using System.Linq;
using SshTool.Core.Storage;
using SshTool.Core.Terminal;
using Xunit;

namespace SshTool.Core.Tests.Terminal
{
    // U11 窗格树（鸿蒙端 16 条用例的等价覆盖：参考仓库不在本机，按任务要点实现）：
    // Split/Close/焦点/比例夹取/叶子枚举/序列化。
    public class PaneTreeTests
    {
        private static PaneTree Tree3()
        {
            // a | (b / c)：a 右分屏出 b，b 再向下分屏出 c
            var tree = new PaneTree("a");
            tree.Split("a", SplitOrientation.Column, "b", 0.5);
            tree.Split("b", SplitOrientation.Row, "c", 0.5);
            return tree;
        }

        [Fact]
        public void NewTree_SingleLeaf()
        {
            var tree = new PaneTree("a");
            Assert.Equal(1, tree.LeafCount);
            Assert.True(tree.Contains("a"));
            Assert.False(tree.Contains("b"));
            Assert.Equal("a", tree.FocusedSessionId);
            Assert.True(tree.Root.IsLeaf);
        }

        [Fact]
        public void Split_LeafBecomesSplitNewPaneSecondAndFocused()
        {
            var tree = new PaneTree("a");
            var split = tree.Split("a", SplitOrientation.Column, "b");

            Assert.False(tree.Root.IsLeaf);
            Assert.Equal(SplitOrientation.Column, split.Orientation);
            Assert.Equal(0.5, split.Ratio, 6);
            Assert.Equal("a", ((LeafNode)split.First).SessionId);
            Assert.Equal("b", ((LeafNode)split.Second).SessionId);
            Assert.Equal("b", tree.FocusedSessionId);
            Assert.Equal(new[] { "a", "b" }, tree.Leaves().Select(l => l.SessionId));
        }

        [Fact]
        public void Split_NestedKeepsOrder()
        {
            var tree = Tree3();
            Assert.Equal(new[] { "a", "b", "c" }, tree.Leaves().Select(l => l.SessionId));
            Assert.Equal(3, tree.LeafCount);
        }

        [Theory]
        [InlineData(0.05, 0.15)]
        [InlineData(0.95, 0.85)]
        [InlineData(0.5, 0.5)]
        [InlineData(double.NaN, 0.5)]
        public void Split_RatioClamped(double input, double expected)
        {
            var tree = new PaneTree("a");
            var split = tree.Split("a", SplitOrientation.Row, "b", input);
            Assert.Equal(expected, split.Ratio, 6);
        }

        [Fact]
        public void Split_UnknownOrDuplicate_Throws()
        {
            var tree = new PaneTree("a");
            Assert.Throws<System.ArgumentException>(() => tree.Split("nope", SplitOrientation.Row, "b"));
            Assert.Throws<System.ArgumentException>(() => tree.Split("a", SplitOrientation.Row, "a"));
        }

        [Fact]
        public void Close_MiddleLeaf_SiblingSubtreeHoisted()
        {
            var tree = Tree3(); // a | (b / c)
            Assert.True(tree.Close("b"));

            // b/c 分支的兄弟是 a？不——关 b 后 c 上提到 b/c 分支位置：a | c
            Assert.Equal(new[] { "a", "c" }, tree.Leaves().Select(l => l.SessionId));
            var root = (SplitNode)tree.Root;
            Assert.Equal(SplitOrientation.Column, root.Orientation);
            Assert.Equal("c", ((LeafNode)root.Second).SessionId);
        }

        [Fact]
        public void Close_FirstLeaf_SplitBecomesRoot()
        {
            var tree = Tree3(); // a | (b / c)
            Assert.True(tree.Close("a"));

            // (b / c) 整体上提为根
            var root = (SplitNode)tree.Root;
            Assert.Equal(SplitOrientation.Row, root.Orientation);
            Assert.Equal(new[] { "b", "c" }, tree.Leaves().Select(l => l.SessionId));
        }

        [Fact]
        public void Close_FocusedLeaf_MovesFocusToSibling()
        {
            var tree = new PaneTree("a");
            tree.Split("a", SplitOrientation.Column, "b"); // 焦点在 b
            tree.Close("b");
            Assert.Equal("a", tree.FocusedSessionId);
        }

        [Fact]
        public void Close_UnfocusedLeaf_KeepsFocus()
        {
            var tree = Tree3(); // 焦点在 c
            tree.SetFocus("b");
            tree.Close("c");
            Assert.Equal("b", tree.FocusedSessionId);
        }

        [Fact]
        public void Close_LastLeaf_Refused()
        {
            var tree = new PaneTree("a");
            Assert.False(tree.Close("a"));
            Assert.True(tree.Contains("a"));
        }

        [Fact]
        public void Close_Unknown_ReturnsFalse()
        {
            var tree = new PaneTree("a");
            Assert.False(tree.Close("nope"));
        }

        [Fact]
        public void SetRatio_Clamped()
        {
            var tree = new PaneTree("a");
            var split = tree.Split("a", SplitOrientation.Column, "b");
            tree.SetRatio(split, 0.01);
            Assert.Equal(0.15, split.Ratio, 6);
            tree.SetRatio(split, 0.99);
            Assert.Equal(0.85, split.Ratio, 6);
        }

        [Fact]
        public void SetFocus_Unknown_ReturnsFalse()
        {
            var tree = new PaneTree("a");
            Assert.False(tree.SetFocus("nope"));
            Assert.Equal("a", tree.FocusedSessionId);
        }

        [Fact]
        public void Serialization_RoundTrip()
        {
            var tree = Tree3();
            tree.SetRatio((SplitNode)tree.Root, 0.3);
            tree.SetFocus("b");

            var restored = PaneTree.FromJson(JsonText.ParseObject(tree.ToJson().ToString(Newtonsoft.Json.Formatting.None)));

            Assert.Equal(new[] { "a", "b", "c" }, restored.Leaves().Select(l => l.SessionId));
            Assert.Equal("b", restored.FocusedSessionId);
            var root = (SplitNode)restored.Root;
            Assert.Equal(SplitOrientation.Column, root.Orientation);
            Assert.Equal(0.3, root.Ratio, 6);
            Assert.Equal(SplitOrientation.Row, ((SplitNode)root.Second).Orientation);
        }

        [Fact]
        public void Serialization_BadJson_Throws()
        {
            Assert.Throws<System.FormatException>(() => PaneTree.FromJson(JsonText.ParseObject(@"{}")));
            Assert.Throws<System.FormatException>(() => PaneTree.FromJson(JsonText.ParseObject(@"{""root"":{""type"":""leaf""}}")));
            Assert.Throws<System.FormatException>(() => PaneTree.FromJson(JsonText.ParseObject(
                @"{""root"":{""type"":""split"",""orientation"":""diagonal"",""first"":{""type"":""leaf"",""sessionId"":""a""},""second"":{""type"":""leaf"",""sessionId"":""b""}}}")));
        }

        [Fact]
        public void Serialization_MissingFocus_FallsBackToFirstLeaf()
        {
            var tree = Tree3();
            var json = tree.ToJson();
            json.Remove("focusedSessionId");

            var restored = PaneTree.FromJson(json);

            Assert.Equal("a", restored.FocusedSessionId);
        }
    }
}
