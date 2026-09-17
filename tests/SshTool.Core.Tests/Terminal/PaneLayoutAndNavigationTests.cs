using System.Linq;
using SshTool.Core.Terminal;
using Xunit;

namespace SshTool.Core.Tests.Terminal
{
    // U11 布局计算与方向导航（验收 ≥6 条）
    public class PaneLayoutAndNavigationTests
    {
        private static PaneRect PaneOf(PaneLayout layout, string sessionId)
        {
            return layout.Panes.Single(p => p.SessionId == sessionId);
        }

        [Fact]
        public void Layout_SingleLeaf_FillsContainer()
        {
            var layout = new PaneTree("a").ComputeLayout(0, 0, 800, 600, 6);

            var pane = Assert.Single(layout.Panes);
            Assert.Equal(new[] { 0.0, 0.0, 800.0, 600.0 }, new[] { pane.X, pane.Y, pane.Width, pane.Height });
            Assert.Empty(layout.Splitters);
        }

        [Fact]
        public void Layout_ColumnSplit()
        {
            var tree = new PaneTree("a");
            tree.Split("a", SplitOrientation.Column, "b", 0.5);

            var layout = tree.ComputeLayout(0, 0, 806, 600, 6);

            var a = PaneOf(layout, "a");
            var b = PaneOf(layout, "b");
            Assert.Equal(new[] { 0.0, 0.0, 400.0, 600.0 }, new[] { a.X, a.Y, a.Width, a.Height });
            Assert.Equal(new[] { 406.0, 0.0, 400.0, 600.0 }, new[] { b.X, b.Y, b.Width, b.Height });
            var splitter = Assert.Single(layout.Splitters);
            Assert.Equal(new[] { 400.0, 0.0, 6.0, 600.0 },
                new[] { splitter.X, splitter.Y, splitter.Width, splitter.Height });
        }

        [Fact]
        public void Layout_RowSplit()
        {
            var tree = new PaneTree("a");
            tree.Split("a", SplitOrientation.Row, "b", 0.25);

            var layout = tree.ComputeLayout(10, 20, 400, 206, 6);

            var a = PaneOf(layout, "a");
            var b = PaneOf(layout, "b");
            Assert.Equal(new[] { 10.0, 20.0, 400.0, 50.0 }, new[] { a.X, a.Y, a.Width, a.Height });
            Assert.Equal(new[] { 10.0, 76.0, 400.0, 150.0 }, new[] { b.X, b.Y, b.Width, b.Height });
            var splitter = Assert.Single(layout.Splitters);
            Assert.Equal(20 + 50, splitter.Y);
            Assert.Equal(6, splitter.Height);
        }

        [Fact]
        public void Layout_NestedSplits()
        {
            // a | (b / c)，根比例 0.5，子比例 0.5；容器 812×412，分隔条 6
            var tree = new PaneTree("a");
            tree.Split("a", SplitOrientation.Column, "b", 0.5);
            tree.Split("b", SplitOrientation.Row, "c", 0.5);

            var layout = tree.ComputeLayout(0, 0, 812, 412, 6);

            Assert.Equal(3, layout.Panes.Count);
            Assert.Equal(2, layout.Splitters.Count);
            var a = PaneOf(layout, "a");
            var b = PaneOf(layout, "b");
            var c = PaneOf(layout, "c");
            Assert.Equal(403, a.Width);                 // (812-6)*0.5
            Assert.Equal(409, b.X);                     // 403+6
            Assert.Equal(403, b.Width);
            Assert.Equal(203, b.Height);                // (412-6)*0.5
            Assert.Equal(209, c.Y);                     // 203+6
            Assert.Equal(203, c.Height);
            // 所有矩形无重叠且覆盖容器：右列 b+c+分隔条 = 412
            Assert.Equal(412, b.Height + 6 + c.Height);
        }

        [Fact]
        public void Layout_SplitterRectReferencesNode()
        {
            var tree = new PaneTree("a");
            var split = tree.Split("a", SplitOrientation.Column, "b", 0.5);

            var layout = tree.ComputeLayout(0, 0, 800, 600, 6);

            Assert.Same(split, layout.Splitters[0].Node);
        }

        // ---------- 方向导航 ----------

        [Fact]
        public void Neighbor_ColumnSplit_LeftRight()
        {
            var tree = new PaneTree("a");
            tree.Split("a", SplitOrientation.Column, "b");

            Assert.Equal("b", tree.FindNeighbor("a", PaneDirection.Right));
            Assert.Equal("a", tree.FindNeighbor("b", PaneDirection.Left));
            Assert.Null(tree.FindNeighbor("a", PaneDirection.Left));
            Assert.Null(tree.FindNeighbor("b", PaneDirection.Right));
        }

        [Fact]
        public void Neighbor_RowSplit_UpDown()
        {
            var tree = new PaneTree("a");
            tree.Split("a", SplitOrientation.Row, "b");

            Assert.Equal("b", tree.FindNeighbor("a", PaneDirection.Down));
            Assert.Equal("a", tree.FindNeighbor("b", PaneDirection.Up));
            Assert.Null(tree.FindNeighbor("a", PaneDirection.Up));
            Assert.Null(tree.FindNeighbor("b", PaneDirection.Down));
        }

        [Fact]
        public void Neighbor_Nested_CrossesSubtree()
        {
            // a | (b / c)
            var tree = new PaneTree("a");
            tree.Split("a", SplitOrientation.Column, "b");
            tree.Split("b", SplitOrientation.Row, "c");

            Assert.Equal("b", tree.FindNeighbor("c", PaneDirection.Up));
            Assert.Equal("c", tree.FindNeighbor("b", PaneDirection.Down));
            Assert.Equal("a", tree.FindNeighbor("b", PaneDirection.Left));
            Assert.Equal("a", tree.FindNeighbor("c", PaneDirection.Left)); // 跨子树到最近叶子
            Assert.Equal("b", tree.FindNeighbor("a", PaneDirection.Right)); // 下到贴近分隔条的叶子
            Assert.Null(tree.FindNeighbor("b", PaneDirection.Up));         // b 在顶边
            Assert.Null(tree.FindNeighbor("c", PaneDirection.Down));       // c 在底边
        }

        [Fact]
        public void Neighbor_SingleLeaf_AlwaysNull()
        {
            var tree = new PaneTree("a");
            Assert.Null(tree.FindNeighbor("a", PaneDirection.Left));
            Assert.Null(tree.FindNeighbor("a", PaneDirection.Down));
        }

        [Fact]
        public void Neighbor_UnknownSession_Null()
        {
            var tree = new PaneTree("a");
            Assert.Null(tree.FindNeighbor("nope", PaneDirection.Right));
        }
    }
}
