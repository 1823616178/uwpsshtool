using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using SshTool.App.Infrastructure;
using SshTool.Core.Common;
using SshTool.Core.Mvvm;
using SshTool.Core.Sessions;
using SshTool.Core.Terminal;

namespace SshTool.App.ViewModels
{
    // U12：宽屏工作区的一个标签页（TabSet.PaneTab 的可观察包装）。
    public sealed class WorkspaceTab : ObservableObject
    {
        private string _title;

        public WorkspaceTab(string tabId, string title, PaneTree tree)
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
            _title = title ?? string.Empty;
            Tree = tree;
        }

        public string TabId { get; private set; }

        public PaneTree Tree { get; private set; }

        public string Title
        {
            get { return _title; }
            set { SetProperty(ref _title, value ?? string.Empty); }
        }
    }

    // U12：多标签 + 每标签一棵 PaneTree。全部突变走 UI 线程调用
    //（调用方均为 UI 事件；会话后台通知经 Prune 里的 DispatcherHelper.Post 回来）。
    // 窄屏只显示聚焦叶子是 TerminalWorkspace 控件的职责，本类不销毁任何窗格树。
    public sealed class WorkspaceViewModel : ViewModelBase
    {
        private readonly TabSet _tabs = new TabSet();
        private readonly SessionManager _sessions;
        private bool _pruning;

        public WorkspaceViewModel(SessionManager sessions)
        {
            _sessions = sessions;
            Tabs = new ObservableCollection<WorkspaceTab>();
            NewTabCommand = new RelayCommand(RequestNewTab);
            if (_sessions != null)
            {
                _sessions.SessionsChanged += OnSessionsChanged;
            }
        }

        public WorkspaceViewModel()
            : this(AppServices.Current != null ? AppServices.Current.Sessions : null)
        {
        }

        public ObservableCollection<WorkspaceTab> Tabs { get; private set; }

        public ICommand NewTabCommand { get; private set; }

        // 结构变化（标签/树/焦点任一改变）。控件重建标签条与窗格布局。
        // 永远在 UI 线程触发（RaiseChanged 内部经 DispatcherHelper.Post 封送）。
        public event EventHandler WorkspaceChanged;

        // 新标签需要选主机：控件弹出主机选择器（02-UI-DESIGN §5.16「打开主机选择」）。
        public event EventHandler HostPickerRequested;

        public SessionManager Sessions
        {
            get { return _sessions; }
        }

        public string ActiveTabId
        {
            get { return _tabs.ActiveTabId; }
        }

        public WorkspaceTab ActiveTab
        {
            get { return FindTab(_tabs.ActiveTabId); }
        }

        public PaneTree ActiveTree
        {
            get
            {
                WorkspaceTab tab = ActiveTab;
                return tab != null ? tab.Tree : null;
            }
        }

        public string FocusedSessionId
        {
            get
            {
                PaneTree tree = ActiveTree;
                return tree != null ? tree.FocusedSessionId : null;
            }
        }

        public void RequestNewTab()
        {
            EventHandler handler = HostPickerRequested;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        // 在新标签中打开主机（Wide 下「选择主机即在右侧开标签，不跳页」）。
        public async Task<WorkspaceTab> OpenHostInNewTabAsync(string hostId)
        {
            if (_sessions == null || string.IsNullOrEmpty(hostId))
            {
                return null;
            }
            SessionInfo info = await _sessions.StartOpenAsync(
                new SessionOpenRequest { HostId = hostId }).ConfigureAwait(true);
            string tabId = IdGenerator.NewId();
            _tabs.Add(tabId, info.Title, info.SessionId);
            SyncTabs();
            RaiseChanged();
            return FindTab(tabId);
        }

        // §5.7 标签右键「复制会话」：把指定标签聚焦窗格的同主机再开一个会话，放进新标签。
        public async Task<WorkspaceTab> DuplicateFocusedSessionAsync(string tabId)
        {
            if (_sessions == null)
            {
                return null;
            }
            WorkspaceTab source = FindTab(tabId);
            if (source == null || source.Tree == null)
            {
                return null;
            }
            SessionInfo focused = GetSession(source.Tree.FocusedSessionId);
            if (focused == null || string.IsNullOrEmpty(focused.HostId))
            {
                return null;
            }
            _tabs.Activate(tabId);
            SessionInfo info = await _sessions.StartOpenAsync(
                new SessionOpenRequest { HostId = focused.HostId }).ConfigureAwait(true);
            string newTabId = IdGenerator.NewId();
            _tabs.Add(newTabId, info.Title, info.SessionId);
            SyncTabs();
            RaiseChanged();
            return FindTab(newTabId);
        }

        // 在聚焦窗格旁分屏：新会话与聚焦窗格同主机，落在 Second 并获得焦点（PaneTree 约定）。
        public async Task<bool> SplitFocusedPaneAsync(SplitOrientation orientation)
        {
            if (_sessions == null)
            {
                return false;
            }
            PaneTree tree = ActiveTree;
            if (tree == null)
            {
                return false;
            }
            SessionInfo focused = GetSession(tree.FocusedSessionId);
            if (focused == null || string.IsNullOrEmpty(focused.HostId))
            {
                return false;
            }
            SessionInfo info = await _sessions.StartOpenAsync(
                new SessionOpenRequest { HostId = focused.HostId }).ConfigureAwait(true);
            tree.Split(tree.FocusedSessionId, orientation, info.SessionId);
            SyncTabs();
            RaiseChanged();
            return true;
        }

        public Task<bool> SplitTabPaneAsync(string tabId, SplitOrientation orientation)
        {
            if (!string.IsNullOrEmpty(tabId) && tabId != _tabs.ActiveTabId)
            {
                _tabs.Activate(tabId);
            }
            return SplitFocusedPaneAsync(orientation);
        }

        public bool ActivateTab(string tabId)
        {
            if (!_tabs.Activate(tabId))
            {
                return false;
            }
            RaiseChanged();
            return true;
        }

        public bool ActivateNext()
        {
            return ActivateNeighbor(1);
        }

        public bool ActivatePrevious()
        {
            return ActivateNeighbor(-1);
        }

        public bool RenameTab(string tabId, string title)
        {
            WorkspaceTab tab = FindTab(tabId);
            string next = title != null ? title.Trim() : null;
            if (tab == null || string.IsNullOrEmpty(next))
            {
                return false;
            }
            tab.Title = next;
            PaneTab source = _tabs.Find(tabId);
            if (source != null)
            {
                source.Title = next;
            }
            RaiseChanged();
            return true;
        }

        // 先摘标签再关会话：SessionsChanged 触发的 Prune 看到标签已不在，不会重复处理。
        public bool CloseTab(string tabId)
        {
            WorkspaceTab tab = FindTab(tabId);
            if (tab == null)
            {
                return false;
            }
            List<string> sessionIds = LeafSessionIds(tab.Tree);
            _tabs.Close(tabId);
            SyncTabs();
            RaiseChanged();
            if (_sessions != null)
            {
                for (int i = 0; i < sessionIds.Count; i++)
                {
                    _sessions.Close(sessionIds[i]);
                }
            }
            return true;
        }

        public void CloseOthers(string keepTabId)
        {
            List<string> ids = new List<string>();
            for (int i = 0; i < Tabs.Count; i++)
            {
                if (Tabs[i].TabId != keepTabId)
                {
                    ids.Add(Tabs[i].TabId);
                }
            }
            for (int i = 0; i < ids.Count; i++)
            {
                CloseTab(ids[i]);
            }
        }

        public bool CloseFocusedPane()
        {
            PaneTree tree = ActiveTree;
            if (tree == null)
            {
                return false;
            }
            return ClosePane(tree.FocusedSessionId);
        }

        // 关窗格：兄弟上提；最后一个叶子则关掉整个标签（PaneTree.Close 约定）。
        public bool ClosePane(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return false;
            }
            WorkspaceTab owner = FindOwner(sessionId);
            if (owner == null)
            {
                return false;
            }
            if (owner.Tree.LeafCount <= 1)
            {
                return CloseTab(owner.TabId);
            }
            if (!owner.Tree.Close(sessionId))
            {
                return false;
            }
            SyncTabs();
            RaiseChanged();
            if (_sessions != null)
            {
                _sessions.Close(sessionId);
            }
            return true;
        }

        public bool FocusPane(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                return false;
            }
            WorkspaceTab owner = FindOwner(sessionId);
            if (owner == null)
            {
                return false;
            }
            if (owner.TabId != _tabs.ActiveTabId)
            {
                _tabs.Activate(owner.TabId);
            }
            if (!owner.Tree.SetFocus(sessionId))
            {
                return false;
            }
            RaiseChanged();
            return true;
        }

        // Ctrl+Alt+方向：按 PaneTree.FindNeighbor 切换聚焦窗格，无邻居时焦点不动。
        public bool MoveFocus(PaneDirection direction)
        {
            PaneTree tree = ActiveTree;
            if (tree == null)
            {
                return false;
            }
            string neighbor = tree.FindNeighbor(tree.FocusedSessionId, direction);
            if (string.IsNullOrEmpty(neighbor))
            {
                return false;
            }
            return FocusPane(neighbor);
        }

        public void SetPaneRatio(SplitNode node, double ratio)
        {
            if (node == null)
            {
                return;
            }
            PaneTree owner = FindTreeForNode(node);
            if (owner == null)
            {
                return;
            }
            owner.SetRatio(node, ratio);
            RaiseChanged();
        }

        // §5.16 快捷键接线（结构动作）；视图局部动作（复制/粘贴/字号/查找）返回 false，
        // 由 TerminalWorkspace 直接操作聚焦的 TerminalView。
        public bool HandleShortcut(ShortcutAction action)
        {
            switch (action)
            {
                case ShortcutAction.NewTab:
                    RequestNewTab();
                    return true;
                case ShortcutAction.ClosePane:
                    return CloseFocusedPane();
                case ShortcutAction.NextTab:
                    return ActivateNext();
                case ShortcutAction.PrevTab:
                    return ActivatePrevious();
                case ShortcutAction.SplitRight:
                    var ignoreRight = SplitFocusedPaneAsync(SplitOrientation.Column);
                    return true;
                case ShortcutAction.SplitDown:
                    var ignoreDown = SplitFocusedPaneAsync(SplitOrientation.Row);
                    return true;
                case ShortcutAction.FocusLeft:
                    return MoveFocus(PaneDirection.Left);
                case ShortcutAction.FocusUp:
                    return MoveFocus(PaneDirection.Up);
                case ShortcutAction.FocusRight:
                    return MoveFocus(PaneDirection.Right);
                case ShortcutAction.FocusDown:
                    return MoveFocus(PaneDirection.Down);
                default:
                    return false;
            }
        }

        public SessionInfo GetSession(string sessionId)
        {
            if (_sessions == null || string.IsNullOrEmpty(sessionId))
            {
                return null;
            }
            try
            {
                IReadOnlyList<SessionInfo> all = _sessions.Sessions;
                for (int i = 0; i < all.Count; i++)
                {
                    if (all[i] != null && all[i].SessionId == sessionId)
                    {
                        return all[i];
                    }
                }
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            return null;
        }

        public WorkspaceTab FindTab(string tabId)
        {
            if (string.IsNullOrEmpty(tabId))
            {
                return null;
            }
            for (int i = 0; i < Tabs.Count; i++)
            {
                if (Tabs[i].TabId == tabId)
                {
                    return Tabs[i];
                }
            }
            return null;
        }

        private WorkspaceTab FindOwner(string sessionId)
        {
            for (int i = 0; i < Tabs.Count; i++)
            {
                if (Tabs[i].Tree != null && Tabs[i].Tree.Contains(sessionId))
                {
                    return Tabs[i];
                }
            }
            return null;
        }

        private PaneTree FindTreeForNode(SplitNode node)
        {
            for (int i = 0; i < Tabs.Count; i++)
            {
                PaneTree tree = Tabs[i].Tree;
                if (tree != null && ContainsNode(tree.Root, node))
                {
                    return tree;
                }
            }
            return null;
        }

        private static bool ContainsNode(PaneNode root, SplitNode target)
        {
            if (root == null || target == null)
            {
                return false;
            }
            if (ReferenceEquals(root, target))
            {
                return true;
            }
            SplitNode split = root as SplitNode;
            if (split == null)
            {
                return false;
            }
            return ContainsNode(split.First, target) || ContainsNode(split.Second, target);
        }

        private bool ActivateNeighbor(int delta)
        {
            if (Tabs.Count < 2)
            {
                return false;
            }
            int index = -1;
            for (int i = 0; i < Tabs.Count; i++)
            {
                if (Tabs[i].TabId == _tabs.ActiveTabId)
                {
                    index = i;
                    break;
                }
            }
            if (index < 0)
            {
                return false;
            }
            int next = (index + delta + Tabs.Count) % Tabs.Count;
            return ActivateTab(Tabs[next].TabId);
        }

        // 会话在别处被关闭（终端页关闭、退出确认 CloseAll）：把死叶子从树上摘掉，
        // 只剩一个死叶子则整标签关闭。窗格树本身不销毁，只修剪。
        private void OnSessionsChanged(object sender, EventArgs e)
        {
            DispatcherHelper.Post(PruneClosedSessions);
        }

        private void PruneClosedSessions()
        {
            if (_sessions == null || _pruning)
            {
                return;
            }
            _pruning = true;
            try
            {
                List<string> deadTabs = new List<string>();
                bool treeChanged = false;
                for (int t = 0; t < Tabs.Count; t++)
                {
                    WorkspaceTab tab = Tabs[t];
                    List<string> leaves = LeafSessionIds(tab.Tree);
                    List<string> dead = new List<string>();
                    for (int i = 0; i < leaves.Count; i++)
                    {
                        if (GetSession(leaves[i]) == null)
                        {
                            dead.Add(leaves[i]);
                        }
                    }
                    if (dead.Count == 0)
                    {
                        continue;
                    }
                    if (dead.Count >= leaves.Count)
                    {
                        deadTabs.Add(tab.TabId);
                        continue;
                    }
                    for (int i = 0; i < dead.Count; i++)
                    {
                        if (tab.Tree.Close(dead[i]))
                        {
                            treeChanged = true;
                        }
                    }
                }
                if (deadTabs.Count > 0 || treeChanged)
                {
                    for (int i = 0; i < deadTabs.Count; i++)
                    {
                        _tabs.Close(deadTabs[i]);
                    }
                    SyncTabs();
                    RaiseChanged();
                }
            }
            finally
            {
                _pruning = false;
            }
        }

        private static List<string> LeafSessionIds(PaneTree tree)
        {
            List<string> ids = new List<string>();
            if (tree == null)
            {
                return ids;
            }
            List<LeafNode> leaves = tree.Leaves();
            for (int i = 0; i < leaves.Count; i++)
            {
                ids.Add(leaves[i].SessionId);
            }
            return ids;
        }

        // 与 TabSet 同步：保留已存在的 WorkspaceTab 实例（标签条选择态稳定），
        // 只增删改 Title。
        private void SyncTabs()
        {
            IReadOnlyList<PaneTab> source = _tabs.Tabs;
            for (int i = Tabs.Count - 1; i >= 0; i--)
            {
                if (_tabs.Find(Tabs[i].TabId) == null)
                {
                    Tabs.RemoveAt(i);
                }
            }
            for (int i = 0; i < source.Count; i++)
            {
                PaneTab paneTab = source[i];
                WorkspaceTab existing = FindTab(paneTab.TabId);
                if (existing == null)
                {
                    Tabs.Add(new WorkspaceTab(paneTab.TabId, paneTab.Title, paneTab.Tree));
                }
                else if (existing.Title != (paneTab.Title ?? string.Empty))
                {
                    existing.Title = paneTab.Title;
                }
            }
        }

        private void RaiseChanged()
        {
            // 后台线程（Prune 已封送、快捷键/菜单均在 UI 线程）兜底：STA 属性必须 UI 线程触碰
            //（前车之鉴 0c3140a WRONG_THREAD 0x8001010E）。
            DispatcherHelper.Post(() =>
            {
                EventHandler handler = WorkspaceChanged;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            });
        }
    }
}
