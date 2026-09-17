using System;

namespace SshTool.Core.Terminal
{
    // 分屏方向：Row = 上下排列（「向下分屏」产生），Column = 左右排列（「向右分屏」产生）。
    public enum SplitOrientation
    {
        Row = 0,
        Column = 1
    }

    public enum PaneDirection
    {
        Left = 0,
        Right = 1,
        Up = 2,
        Down = 3
    }

    public abstract class PaneNode
    {
        internal SplitNode Parent { get; set; }

        public bool IsLeaf
        {
            get { return this is LeafNode; }
        }
    }

    // 叶子：绑定一个会话
    public sealed class LeafNode : PaneNode
    {
        public LeafNode(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                throw new ArgumentException("sessionId 不能为空", nameof(sessionId));
            }
            SessionId = sessionId;
        }

        public string SessionId { get; private set; }
    }

    // 分支：二分。Ratio 为 First 的占比，夹在 [0.15, 0.85]
    public sealed class SplitNode : PaneNode
    {
        public const double MinRatio = 0.15;
        public const double MaxRatio = 0.85;

        public SplitNode(SplitOrientation orientation, PaneNode first, PaneNode second, double ratio)
        {
            Orientation = orientation;
            First = first;
            Second = second;
            first.Parent = this;
            second.Parent = this;
            Ratio = ClampRatio(ratio);
        }

        public SplitOrientation Orientation { get; private set; }
        public PaneNode First { get; private set; }
        public PaneNode Second { get; private set; }
        public double Ratio { get; internal set; }

        public static double ClampRatio(double ratio)
        {
            if (double.IsNaN(ratio))
            {
                return 0.5;
            }
            return Math.Min(MaxRatio, Math.Max(MinRatio, ratio));
        }

        // 用另一节点替换直接子节点（关闭叶子上提时用）
        internal void ReplaceChild(PaneNode oldChild, PaneNode newChild)
        {
            if (ReferenceEquals(First, oldChild))
            {
                First = newChild;
            }
            else if (ReferenceEquals(Second, oldChild))
            {
                Second = newChild;
            }
            else
            {
                throw new ArgumentException("不是该分支的子节点", nameof(oldChild));
            }
            newChild.Parent = this;
        }
    }
}
