using System.Collections.Generic;

namespace SshTool.Core.Terminal
{
    // 一个窗格的布局矩形（逻辑像素/epx，由调用方坐标系决定）
    public struct PaneRect
    {
        public string SessionId;
        public double X;
        public double Y;
        public double Width;
        public double Height;
    }

    // 分隔条矩形：Node 供拖动时回调 PaneTree.SetRatio
    public struct SplitterRect
    {
        public SplitNode Node;
        public double X;
        public double Y;
        public double Width;
        public double Height;
    }

    // 布局计算结果：叶子矩形 + 分隔条矩形（绘制/命中测试用）
    public sealed class PaneLayout
    {
        public List<PaneRect> Panes { get; private set; } = new List<PaneRect>();
        public List<SplitterRect> Splitters { get; private set; } = new List<SplitterRect>();
    }
}
