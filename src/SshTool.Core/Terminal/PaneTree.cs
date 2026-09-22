using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace SshTool.Core.Terminal
{
    // U11 窗格树：一个标签页内的分屏二叉树。叶子绑定 sessionId；分支带方向与占比
    // （First 占比夹在 0.15–0.85）。新窗格固定落在 Second（右/下）并获得焦点。
    public sealed class PaneTree
    {
        private PaneNode _root;

        public PaneTree(string sessionId)
        {
            _root = new LeafNode(sessionId);
            FocusedSessionId = sessionId;
        }

        private PaneTree(PaneNode root, string focusedSessionId)
        {
            _root = root;
            FocusedSessionId = focusedSessionId;
        }

        public PaneNode Root
        {
            get { return _root; }
        }

        public string FocusedSessionId { get; private set; }

        public int LeafCount
        {
            get { return Leaves().Count; }
        }

        // 有序枚举（First 先于 Second，即视觉上的从左到右/从上到下）
        public List<LeafNode> Leaves()
        {
            var result = new List<LeafNode>();
            CollectLeaves(_root, result);
            return result;
        }

        private static void CollectLeaves(PaneNode node, List<LeafNode> result)
        {
            var leaf = node as LeafNode;
            if (leaf != null)
            {
                result.Add(leaf);
                return;
            }
            var split = (SplitNode)node;
            CollectLeaves(split.First, result);
            CollectLeaves(split.Second, result);
        }

        public bool Contains(string sessionId)
        {
            return FindLeaf(sessionId) != null;
        }

        // O14：直接在树里找，不再先 Leaves() 建一张表——FindLeaf 被 Contains /
        // Split / Close / SetFocus / FindNeighbor 全部经由，每次查找都分配一个
        // List 没必要。遍历顺序与 Leaves() 一致（First 先于 Second）。
        public LeafNode FindLeaf(string sessionId)
        {
            return FindLeafIn(_root, sessionId);
        }

        private static LeafNode FindLeafIn(PaneNode node, string sessionId)
        {
            var leaf = node as LeafNode;
            if (leaf != null)
            {
                return leaf.SessionId == sessionId ? leaf : null;
            }
            var split = (SplitNode)node;
            return FindLeafIn(split.First, sessionId) ?? FindLeafIn(split.Second, sessionId);
        }

        // 在 sessionId 的窗格旁分屏：新窗格在 Second（向右/向下），焦点移到新窗格。
        // ratio 为分屏后原窗格（First）的占比。
        public SplitNode Split(string sessionId, SplitOrientation orientation, string newSessionId, double ratio = 0.5)
        {
            var leaf = FindLeaf(sessionId);
            if (leaf == null)
            {
                throw new ArgumentException("窗格不存在: " + sessionId, nameof(sessionId));
            }
            if (Contains(newSessionId))
            {
                throw new ArgumentException("sessionId 重复: " + newSessionId, nameof(newSessionId));
            }
            // 必须先取原父节点：SplitNode 构造器会把 leaf.Parent 改写为新分支
            var parent = leaf.Parent;
            var split = new SplitNode(orientation, leaf, new LeafNode(newSessionId), ratio);
            if (parent == null)
            {
                split.Parent = null;
                _root = split;
            }
            else
            {
                parent.ReplaceChild(leaf, split);
            }
            FocusedSessionId = newSessionId;
            return split;
        }

        // 关闭叶子：兄弟子树上提到父分支位置。最后一个叶子不可关（返回 false，
        // 由 TabSet 决定关标签）。焦点在被关窗格时移到上提子树的第一个叶子。
        public bool Close(string sessionId)
        {
            var leaf = FindLeaf(sessionId);
            if (leaf == null)
            {
                return false;
            }
            var parent = leaf.Parent;
            if (parent == null)
            {
                return false; // 最后一个叶子
            }
            var sibling = ReferenceEquals(parent.First, leaf) ? parent.Second : parent.First;
            var grandparent = parent.Parent;
            if (grandparent == null)
            {
                sibling.Parent = null;
                _root = sibling;
            }
            else
            {
                grandparent.ReplaceChild(parent, sibling);
            }
            if (FocusedSessionId == sessionId)
            {
                FocusedSessionId = FirstLeafOf(sibling).SessionId;
            }
            return true;
        }

        public bool SetFocus(string sessionId)
        {
            if (!Contains(sessionId))
            {
                return false;
            }
            FocusedSessionId = sessionId;
            return true;
        }

        public void SetRatio(SplitNode node, double ratio)
        {
            if (node == null)
            {
                throw new ArgumentNullException(nameof(node));
            }
            node.Ratio = SplitNode.ClampRatio(ratio);
        }

        // 树形方向导航：沿祖先找轴向匹配且处于可穿越侧的分支，下到对面子树最贴近分隔条的叶子；
        // 无则 null（焦点不动）。
        public string FindNeighbor(string sessionId, PaneDirection direction)
        {
            var leaf = FindLeaf(sessionId);
            if (leaf == null)
            {
                return null;
            }
            bool horizontal = direction == PaneDirection.Left || direction == PaneDirection.Right;
            bool towardFirst = direction == PaneDirection.Left || direction == PaneDirection.Up;
            PaneNode node = leaf;
            while (node.Parent != null)
            {
                var split = node.Parent;
                bool axisMatch = (split.Orientation == SplitOrientation.Column) == horizontal;
                bool onTraversableSide = towardFirst
                    ? ReferenceEquals(node, split.Second)
                    : ReferenceEquals(node, split.First);
                if (axisMatch && onTraversableSide)
                {
                    var target = towardFirst ? split.First : split.Second;
                    return DescendEdge(target, horizontal, towardFirst).SessionId;
                }
                node = split;
            }
            return null;
        }

        // 下到子树里最贴近被穿越分隔条的叶子：匹配轴取近侧（向左/上穿越 → 取右/下缘），
        // 另一轴不改变该方向上的位置，任取 First 保持确定。
        private static LeafNode DescendEdge(PaneNode node, bool horizontal, bool towardFirst)
        {
            while (true)
            {
                var leaf = node as LeafNode;
                if (leaf != null)
                {
                    return leaf;
                }
                var split = (SplitNode)node;
                bool splitHorizontal = split.Orientation == SplitOrientation.Column;
                if (splitHorizontal == horizontal)
                {
                    node = towardFirst ? split.Second : split.First;
                }
                else
                {
                    node = split.First;
                }
            }
        }

        private static LeafNode FirstLeafOf(PaneNode node)
        {
            while (true)
            {
                var leaf = node as LeafNode;
                if (leaf != null)
                {
                    return leaf;
                }
                node = ((SplitNode)node).First;
            }
        }

        // ---------- 布局 ----------

        // 容器矩形 → 叶子矩形与分隔条矩形。splitterThickness 取视觉厚度（§5.x：6 epx）。
        public PaneLayout ComputeLayout(double x, double y, double width, double height, double splitterThickness)
        {
            var layout = new PaneLayout();
            LayoutNode(_root, x, y, width, height, splitterThickness, layout);
            return layout;
        }

        private static void LayoutNode(
            PaneNode node, double x, double y, double width, double height,
            double splitterThickness, PaneLayout layout)
        {
            var leaf = node as LeafNode;
            if (leaf != null)
            {
                layout.Panes.Add(new PaneRect
                {
                    SessionId = leaf.SessionId,
                    X = x,
                    Y = y,
                    Width = Math.Max(0, width),
                    Height = Math.Max(0, height)
                });
                return;
            }
            var split = (SplitNode)node;
            if (split.Orientation == SplitOrientation.Column)
            {
                double available = Math.Max(0, width - splitterThickness);
                double firstWidth = Math.Floor(available * split.Ratio);
                LayoutNode(split.First, x, y, firstWidth, height, splitterThickness, layout);
                layout.Splitters.Add(new SplitterRect
                {
                    Node = split,
                    X = x + firstWidth,
                    Y = y,
                    Width = Math.Min(splitterThickness, Math.Max(0, width - firstWidth)),
                    Height = Math.Max(0, height)
                });
                double secondX = x + firstWidth + splitterThickness;
                LayoutNode(split.Second, secondX, y, Math.Max(0, x + width - secondX), height,
                    splitterThickness, layout);
            }
            else
            {
                double available = Math.Max(0, height - splitterThickness);
                double firstHeight = Math.Floor(available * split.Ratio);
                LayoutNode(split.First, x, y, width, firstHeight, splitterThickness, layout);
                layout.Splitters.Add(new SplitterRect
                {
                    Node = split,
                    X = x,
                    Y = y + firstHeight,
                    Width = Math.Max(0, width),
                    Height = Math.Min(splitterThickness, Math.Max(0, height - firstHeight))
                });
                double secondY = y + firstHeight + splitterThickness;
                LayoutNode(split.Second, x, secondY, width, Math.Max(0, y + height - secondY),
                    splitterThickness, layout);
            }
        }

        // ---------- 序列化（会话恢复快照 state/sessions.json，01-DESIGN §8.1） ----------

        public JObject ToJson()
        {
            return new JObject
            {
                ["root"] = NodeToJson(_root),
                ["focusedSessionId"] = FocusedSessionId
            };
        }

        private static JObject NodeToJson(PaneNode node)
        {
            var leaf = node as LeafNode;
            if (leaf != null)
            {
                return new JObject
                {
                    ["type"] = "leaf",
                    ["sessionId"] = leaf.SessionId
                };
            }
            var split = (SplitNode)node;
            return new JObject
            {
                ["type"] = "split",
                ["orientation"] = split.Orientation == SplitOrientation.Row ? "row" : "column",
                ["ratio"] = split.Ratio,
                ["first"] = NodeToJson(split.First),
                ["second"] = NodeToJson(split.Second)
            };
        }

        public static PaneTree FromJson(JObject json)
        {
            if (json == null)
            {
                throw new FormatException("窗格树快照为空");
            }
            var rootToken = json["root"];
            if (rootToken == null || rootToken.Type != JTokenType.Object)
            {
                throw new FormatException("窗格树快照缺 root");
            }
            var root = NodeFromJson((JObject)rootToken);
            var focused = json["focusedSessionId"] != null && json["focusedSessionId"].Type == JTokenType.String
                ? (string)json["focusedSessionId"]
                : FirstLeafOf(root).SessionId;
            return new PaneTree(root, focused);
        }

        private static PaneNode NodeFromJson(JObject json)
        {
            var type = json["type"] != null && json["type"].Type == JTokenType.String
                ? (string)json["type"]
                : null;
            if (type == "leaf")
            {
                var sessionId = json["sessionId"] != null && json["sessionId"].Type == JTokenType.String
                    ? (string)json["sessionId"]
                    : null;
                if (string.IsNullOrEmpty(sessionId))
                {
                    throw new FormatException("叶子缺 sessionId");
                }
                return new LeafNode(sessionId);
            }
            if (type == "split")
            {
                var orientationText = json["orientation"] != null && json["orientation"].Type == JTokenType.String
                    ? (string)json["orientation"]
                    : null;
                if (orientationText != "row" && orientationText != "column")
                {
                    throw new FormatException("分支 orientation 非法: " + orientationText);
                }
                var firstToken = json["first"] as JObject;
                var secondToken = json["second"] as JObject;
                if (firstToken == null || secondToken == null)
                {
                    throw new FormatException("分支缺 first/second");
                }
                double ratio = 0.5;
                var ratioToken = json["ratio"];
                if (ratioToken != null && ratioToken.Type == JTokenType.Float)
                {
                    ratio = (double)ratioToken;
                }
                else if (ratioToken != null && ratioToken.Type == JTokenType.Integer)
                {
                    ratio = (double)(long)ratioToken;
                }
                return new SplitNode(
                    orientationText == "row" ? SplitOrientation.Row : SplitOrientation.Column,
                    NodeFromJson(firstToken), NodeFromJson(secondToken), ratio);
            }
            throw new FormatException("未知节点类型: " + type);
        }
    }
}
