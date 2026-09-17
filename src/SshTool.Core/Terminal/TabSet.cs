using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace SshTool.Core.Terminal
{
    // 一个标签页：标题 + 窗格树
    public sealed class PaneTab
    {
        public PaneTab(string tabId, string title, PaneTree tree)
        {
            if (string.IsNullOrEmpty(tabId))
            {
                throw new ArgumentException("tabId 不能为空", nameof(tabId));
            }
            if (tree == null)
            {
                throw new ArgumentNullException(nameof(tree));
            }
            TabId = tabId;
            Title = title;
            Tree = tree;
        }

        public string TabId { get; private set; }
        public string Title { get; set; }
        public PaneTree Tree { get; private set; }
    }

    // U11 标签集合：有序标签列表 + 活动标签。关闭活动标签时激活右侧邻居，无右侧则左侧。
    public sealed class TabSet
    {
        private readonly List<PaneTab> _tabs = new List<PaneTab>();

        public event EventHandler Changed;

        public IReadOnlyList<PaneTab> Tabs
        {
            get { return _tabs; }
        }

        public string ActiveTabId { get; private set; }

        public PaneTab ActiveTab
        {
            get { return Find(ActiveTabId); }
        }

        // id 由调用方提供（SessionManager/恢复快照），保证可测试与可恢复
        public PaneTab Add(string tabId, string title, string sessionId)
        {
            if (Find(tabId) != null)
            {
                throw new ArgumentException("tabId 重复: " + tabId, nameof(tabId));
            }
            var tab = new PaneTab(tabId, title, new PaneTree(sessionId));
            _tabs.Add(tab);
            ActiveTabId = tabId;
            RaiseChanged();
            return tab;
        }

        public bool Close(string tabId)
        {
            int index = IndexOf(tabId);
            if (index < 0)
            {
                return false;
            }
            _tabs.RemoveAt(index);
            if (ActiveTabId == tabId)
            {
                // 优先右侧（关闭后同下标即原右侧），无右侧则左侧
                ActiveTabId = _tabs.Count == 0
                    ? null
                    : _tabs[Math.Min(index, _tabs.Count - 1)].TabId;
            }
            RaiseChanged();
            return true;
        }

        public bool Activate(string tabId)
        {
            if (Find(tabId) == null)
            {
                return false;
            }
            if (ActiveTabId != tabId)
            {
                ActiveTabId = tabId;
                RaiseChanged();
            }
            return true;
        }

        public PaneTab Find(string tabId)
        {
            for (int i = 0; i < _tabs.Count; i++)
            {
                if (_tabs[i].TabId == tabId)
                {
                    return _tabs[i];
                }
            }
            return null;
        }

        private int IndexOf(string tabId)
        {
            for (int i = 0; i < _tabs.Count; i++)
            {
                if (_tabs[i].TabId == tabId)
                {
                    return i;
                }
            }
            return -1;
        }

        private void RaiseChanged()
        {
            var handler = Changed;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        // ---------- 序列化（会话恢复快照） ----------

        public JObject ToJson()
        {
            var tabs = new JArray();
            foreach (var tab in _tabs)
            {
                tabs.Add(new JObject
                {
                    ["tabId"] = tab.TabId,
                    ["title"] = tab.Title,
                    ["tree"] = tab.Tree.ToJson()
                });
            }
            return new JObject
            {
                ["tabs"] = tabs,
                ["activeTabId"] = ActiveTabId
            };
        }

        public static TabSet FromJson(JObject json)
        {
            if (json == null)
            {
                throw new FormatException("标签快照为空");
            }
            var tabsToken = json["tabs"] as JArray;
            if (tabsToken == null)
            {
                throw new FormatException("标签快照缺 tabs");
            }
            var set = new TabSet();
            foreach (var token in tabsToken)
            {
                var tabJson = token as JObject;
                var treeJson = tabJson != null ? tabJson["tree"] as JObject : null;
                var tabId = tabJson != null && tabJson["tabId"] != null && tabJson["tabId"].Type == JTokenType.String
                    ? (string)tabJson["tabId"]
                    : null;
                if (tabId == null || treeJson == null)
                {
                    throw new FormatException("标签快照项缺 tabId/tree");
                }
                if (set.Find(tabId) != null)
                {
                    throw new FormatException("tabId 重复: " + tabId);
                }
                set._tabs.Add(new PaneTab(tabId,
                    tabJson["title"] != null && tabJson["title"].Type == JTokenType.String
                        ? (string)tabJson["title"]
                        : null,
                    PaneTree.FromJson(treeJson)));
            }
            var active = json["activeTabId"] != null && json["activeTabId"].Type == JTokenType.String
                ? (string)json["activeTabId"]
                : null;
            set.ActiveTabId = set.Find(active) != null
                ? active
                : set._tabs.Count > 0 ? set._tabs[0].TabId : null;
            return set;
        }
    }
}
