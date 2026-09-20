using System;
using System.Collections.Generic;
using System.ComponentModel;
using SshTool.App.Infrastructure;
using SshTool.App.Terminal;
using SshTool.App.ViewModels;
using SshTool.Core.Appearance;
using SshTool.Core.Models;
using SshTool.Core.Sessions;
using SshTool.Core.Terminal;
using Windows.ApplicationModel.Resources;
using Windows.Foundation;
using Windows.UI.Core;
using Windows.UI.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace SshTool.App.Controls
{
    // U12：宽屏会话区。活动标签的 PaneTree 经 PaneLayout.ComputeLayout 绝对定位：
    // 每个叶子一个 TerminalView（焦点边框），分支一条可拖动分隔条。
    // 不可见窗格（非活动标签/窄屏非聚焦叶）Visibility=Collapsed + SetRenderingActive(false)，
    // 不绘制且退订 FrameScheduler；窗格树与 TerminalView 实例保留，可切回。
    public sealed partial class TerminalWorkspace : UserControl
    {
        private WorkspaceViewModel _viewModel;
        private readonly Dictionary<string, TerminalView> _views = new Dictionary<string, TerminalView>();
        private readonly Dictionary<string, Border> _chrome = new Dictionary<string, Border>();
        private readonly Dictionary<string, StackPanel> _hoverBars = new Dictionary<string, StackPanel>();
        private readonly Dictionary<string, SessionInfo> _bound = new Dictionary<string, SessionInfo>();
        private readonly Dictionary<SplitNode, Border> _splitters = new Dictionary<SplitNode, Border>();
        private CoreCursor _savedCursor;

        private bool _dragging;
        private SplitNode _dragNode;
        private uint _dragPointerId;
        private Point _dragStart;
        private double _dragStartRatio;
        private bool _appearanceSubscribed;

        public TerminalWorkspace()
        {
            this.InitializeComponent();
            this.Unloaded += OnUnloaded;
            this.SizeChanged += OnSizeChanged;
        }

        public void Attach(WorkspaceViewModel viewModel)
        {
            if (ReferenceEquals(_viewModel, viewModel))
            {
                return;
            }
            if (_viewModel != null)
            {
                _viewModel.WorkspaceChanged -= OnWorkspaceChanged;
                _viewModel.HostPickerRequested -= OnHostPickerRequested;
            }
            _viewModel = viewModel;
            Strip.Attach(viewModel);
            if (_viewModel != null)
            {
                _viewModel.WorkspaceChanged += OnWorkspaceChanged;
                _viewModel.HostPickerRequested += OnHostPickerRequested;
                Empty.PrimaryCommand = _viewModel.NewTabCommand;
            }
            SubscribeAppearance();
            Rebuild();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_viewModel != null)
            {
                _viewModel.WorkspaceChanged -= OnWorkspaceChanged;
                _viewModel.HostPickerRequested -= OnHostPickerRequested;
            }
            UnsubscribeAppearance();
            // SessionInfo 是 SessionManager 长周期持有：回导航重建本控件时，
            // 旧订阅不摘会导致旧控件/旧视图泄漏。
            foreach (KeyValuePair<string, SessionInfo> pair in _bound)
            {
                if (pair.Value != null)
                {
                    pair.Value.PropertyChanged -= OnSessionPropertyChanged;
                }
            }
            _bound.Clear();
        }

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_dragging)
            {
                LayoutAll();
                return;
            }
            // 首次度量到达前 EnsureSplitters 建不出分隔条，这里走全量重建；
            // 拖动中则只搬位置（保指针捕获）。
            Rebuild();
        }

        private void OnWorkspaceChanged(object sender, EventArgs e)
        {
            if (!DispatcherHelper.HasThreadAccess)
            {
                DispatcherHelper.Post(() => OnWorkspaceChanged(sender, e));
                return;
            }
            if (_dragging)
            {
                // 拖动中只搬位置：分隔条元素一旦重建，指针捕获就丢了。
                LayoutAll();
                return;
            }
            Rebuild();
        }

        private void Rebuild()
        {
            if (!DispatcherHelper.HasThreadAccess)
            {
                DispatcherHelper.Post(Rebuild);
                return;
            }
            EnsurePanes();
            EnsureSplitters();
            LayoutAll();
            UpdateEmpty();
        }

        // ---------- 窗格 ----------

        private void EnsurePanes()
        {
            if (_viewModel == null)
            {
                return;
            }
            HashSet<string> live = LiveSessionIds();
            // 1) 剪掉已不在任何标签里的视图（标签关闭时会话已由 VM 关掉，不会复用）。
            List<string> dead = new List<string>();
            foreach (string id in _views.Keys)
            {
                if (!live.Contains(id))
                {
                    dead.Add(id);
                }
            }
            for (int i = 0; i < dead.Count; i++)
            {
                RemoveView(dead[i]);
            }
            // 2) 活动标签叶子确保有视图并绑定会话。
            WorkspaceTab active = _viewModel.ActiveTab;
            if (active == null || active.Tree == null)
            {
                return;
            }
            List<LeafNode> leaves = active.Tree.Leaves();
            for (int i = 0; i < leaves.Count; i++)
            {
                EnsureView(leaves[i].SessionId);
            }
        }

        private HashSet<string> LiveSessionIds()
        {
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            if (_viewModel == null)
            {
                return ids;
            }
            for (int t = 0; t < _viewModel.Tabs.Count; t++)
            {
                PaneTree tree = _viewModel.Tabs[t].Tree;
                if (tree == null)
                {
                    continue;
                }
                List<LeafNode> leaves = tree.Leaves();
                for (int i = 0; i < leaves.Count; i++)
                {
                    ids.Add(leaves[i].SessionId);
                }
            }
            return ids;
        }

        private void EnsureView(string sessionId)
        {
            TerminalView view;
            if (!_views.TryGetValue(sessionId, out view) || view == null)
            {
                view = new TerminalView();
                view.Shortcut += OnPaneShortcut;
                _views[sessionId] = view;
            }
            Border frame;
            if (!_chrome.TryGetValue(sessionId, out frame) || frame == null)
            {
                frame = BuildChrome(sessionId, view);
                _chrome[sessionId] = frame;
                PaneHost.Children.Add(frame);
            }
            BindSession(sessionId, view);
        }

        private void RemoveView(string sessionId)
        {
            TerminalView view;
            if (_views.TryGetValue(sessionId, out view))
            {
                _views.Remove(sessionId);
                if (view != null)
                {
                    view.Shortcut -= OnPaneShortcut;
                    view.Session = null;
                }
            }
            SessionInfo bound;
            if (_bound.TryGetValue(sessionId, out bound))
            {
                _bound.Remove(sessionId);
                if (bound != null)
                {
                    bound.PropertyChanged -= OnSessionPropertyChanged;
                }
            }
            Border frame;
            if (_chrome.TryGetValue(sessionId, out frame))
            {
                _chrome.Remove(sessionId);
                if (frame != null && PaneHost.Children.Contains(frame))
                {
                    PaneHost.Children.Remove(frame);
                }
            }
            _hoverBars.Remove(sessionId);
        }

        private void BindSession(string sessionId, TerminalView view)
        {
            if (_viewModel == null)
            {
                return;
            }
            SessionInfo info = _viewModel.GetSession(sessionId);
            if (info == null)
            {
                return;
            }
            if (!_bound.ContainsKey(sessionId))
            {
                _bound[sessionId] = info;
                info.PropertyChanged += OnSessionPropertyChanged;
            }
            else
            {
                _bound[sessionId] = info;
            }
            try
            {
                if (!ReferenceEquals(view.Session, info.NativeSession))
                {
                    view.Session = info.NativeSession;
                }
            }
            catch (Exception)
            {
            }
            // A03：叶子绑定会话时按主机外观初始化（之后 Changed 事件里刷新）。
            var ignoreAppearance = AppearanceApplier.ApplyForHostAsync(view, info.HostId);
        }

        private void OnSessionPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            SessionInfo info = sender as SessionInfo;
            if (info == null)
            {
                return;
            }
            if (!string.IsNullOrEmpty(e.PropertyName) && e.PropertyName != "NativeSession")
            {
                return;
            }
            // NativeSession 切换碰 Canvas/DispatcherTimer，必须回 UI 线程
            //（前车之鉴 0c3140a WRONG_THREAD 0x8001010E）。
            DispatcherHelper.Post(() =>
            {
                TerminalView view;
                if (_views.TryGetValue(info.SessionId, out view) && view != null)
                {
                    try
                    {
                        view.Session = info.NativeSession;
                    }
                    catch (Exception)
                    {
                    }
                }
            });
        }

        // A03：外观变化后刷新所有可见叶子的调色板与字体度量（不重连）。
        private void SubscribeAppearance()
        {
            if (_appearanceSubscribed)
            {
                return;
            }
            if (AppServices.Current == null || AppServices.Current.AppearanceService == null)
            {
                return;
            }
            AppServices.Current.AppearanceService.Changed += OnAppearanceChanged;
            _appearanceSubscribed = true;
        }

        private void UnsubscribeAppearance()
        {
            if (!_appearanceSubscribed)
            {
                return;
            }
            _appearanceSubscribed = false;
            if (AppServices.Current != null && AppServices.Current.AppearanceService != null)
            {
                AppServices.Current.AppearanceService.Changed -= OnAppearanceChanged;
            }
        }

        private void OnAppearanceChanged(object sender, AppearanceChangedEventArgs e)
        {
            if (!DispatcherHelper.HasThreadAccess)
            {
                DispatcherHelper.Post(() => OnAppearanceChanged(sender, e));
                return;
            }
            foreach (KeyValuePair<string, TerminalView> pair in _views)
            {
                if (pair.Value == null)
                {
                    continue;
                }
                string hostId = HostIdOf(pair.Key);
                if (!AppearanceApplier.NeedsRefresh(e, hostId))
                {
                    continue;
                }
                var ignore = AppearanceApplier.ApplyForHostAsync(pair.Value, hostId);
            }
        }

        private string HostIdOf(string sessionId)
        {
            SessionInfo bound;
            if (_bound.TryGetValue(sessionId, out bound) && bound != null)
            {
                return bound.HostId;
            }
            if (_viewModel != null)
            {
                SessionInfo info = _viewModel.GetSession(sessionId);
                if (info != null)
                {
                    return info.HostId;
                }
            }
            return null;
        }

        private Border BuildChrome(string sessionId, TerminalView view)
        {
            var frame = new Border();
            frame.Tag = sessionId;
            frame.BorderThickness = (Thickness)Application.Current.Resources["BorderThin"];
            frame.Background = Banner.ResolveThemedBrush("AppBgBrush");
            frame.Tapped += OnPaneTapped;
            frame.Holding += OnPaneHolding;
            frame.PointerEntered += OnPanePointerEntered;
            frame.PointerExited += OnPanePointerExited;

            var grid = new Grid();
            view.HorizontalAlignment = HorizontalAlignment.Stretch;
            view.VerticalAlignment = VerticalAlignment.Stretch;
            grid.Children.Add(view);

            StackPanel bar = BuildHoverBar(sessionId);
            bar.HorizontalAlignment = HorizontalAlignment.Right;
            bar.VerticalAlignment = VerticalAlignment.Top;
            bar.Visibility = Visibility.Collapsed;
            grid.Children.Add(bar);
            _hoverBars[sessionId] = bar;

            frame.Child = grid;
            frame.Visibility = Visibility.Collapsed;
            return frame;
        }

        private StackPanel BuildHoverBar(string sessionId)
        {
            var bar = new StackPanel();
            bar.Orientation = Orientation.Horizontal;
            bar.Background = Banner.ResolveThemedBrush("AppSurfaceBrush");
            bar.Tag = sessionId;

            // C-03：悬浮条按钮套 TerminalIconButtonStyle（§7.5 AllowFocusOnInteraction=False，
            // 防抢哨兵焦点）；悬浮条无行高约束，显式给触控高。无障碍名走 resw。
            Style iconStyle = (Style)Application.Current.Resources["TerminalIconButtonStyle"];
            double touchSize = (double)Application.Current.Resources["TouchTargetMin"];
            ResourceLoader loader = ResourceLoader.GetForCurrentView();

            var split = new Button();
            split.Style = iconStyle;
            split.Height = touchSize;
            // V01a：⧉ 换 MDL2 字形。MDL2 无带方向的分屏图标（既有 IconSplitH/V 经渲染核对
            // 是圆角/矩形占位，非分屏），且本按钮弹出「向右/向下」两个方向，语义上是通用
            // 「分屏」——用 IconSplit（E8A9 2×2 窗格阵列）。字形大小与同条关闭按钮一致（默认）。
            split.Content = new FontIcon
            {
                Glyph = (string)Application.Current.Resources["IconSplit"]
            };
            string splitA11yName = loader.GetString("Workspace_SplitButton_A11yName");
            if (!string.IsNullOrEmpty(splitA11yName))
            {
                Windows.UI.Xaml.Automation.AutomationProperties.SetName(split, splitA11yName);
            }
            split.Tag = sessionId;
            split.Click += OnPaneSplitClick;
            bar.Children.Add(split);

            var close = new Button();
            // UI 走查：✕ 文本换 IconClose 字形并补无障碍名（与 TabStrip/SessionsPivot 一致）。
            close.Style = iconStyle;
            close.Height = touchSize;
            close.Content = new FontIcon
            {
                Glyph = (string)Application.Current.Resources["IconClose"]
            };
            string closeA11yName = loader.GetString("Workspace_CloseButton_A11yName");
            if (!string.IsNullOrEmpty(closeA11yName))
            {
                Windows.UI.Xaml.Automation.AutomationProperties.SetName(close, closeA11yName);
            }
            close.Tag = sessionId;
            close.Click += OnPaneCloseClick;
            bar.Children.Add(close);
            return bar;
        }

        // ---------- 分隔条 ----------

        private void EnsureSplitters()
        {
            List<SplitNode> dead = new List<SplitNode>();
            foreach (SplitNode node in _splitters.Keys)
            {
                dead.Add(node);
            }
            for (int i = 0; i < dead.Count; i++)
            {
                Border element = _splitters[dead[i]];
                _splitters.Remove(dead[i]);
                if (element != null && PaneHost.Children.Contains(element))
                {
                    PaneHost.Children.Remove(element);
                }
            }
            if (_dragging)
            {
                return;
            }
            if (PaneHost != null && IsNarrow(PaneHost.ActualWidth))
            {
                // 窄屏只显示聚焦叶，不建分隔条。
                return;
            }
            PaneLayout layout = ComputeLayout();
            if (layout == null)
            {
                return;
            }
            for (int i = 0; i < layout.Splitters.Count; i++)
            {
                SplitterRect rect = layout.Splitters[i];
                Border element = BuildSplitter(rect.Node);
                _splitters[rect.Node] = element;
                PaneHost.Children.Add(element);
            }
        }

        private Border BuildSplitter(SplitNode node)
        {
            bool vertical = node.Orientation == SplitOrientation.Column;
            var outer = new Border();
            outer.Tag = node;
            outer.Background = new SolidColorBrush(Windows.UI.Colors.Transparent);
            if (vertical)
            {
                outer.Width = (double)Application.Current.Resources["SpaceLg"];
            }
            else
            {
                outer.Height = (double)Application.Current.Resources["SpaceLg"];
            }
            var inner = new Border();
            inner.Background = Banner.ResolveThemedBrush("AppBorderBrush");
            if (vertical)
            {
                inner.Width = (double)Application.Current.Resources["SplitterThickness"];
                inner.HorizontalAlignment = HorizontalAlignment.Center;
                inner.VerticalAlignment = VerticalAlignment.Stretch;
            }
            else
            {
                inner.Height = (double)Application.Current.Resources["SplitterThickness"];
                inner.HorizontalAlignment = HorizontalAlignment.Stretch;
                inner.VerticalAlignment = VerticalAlignment.Center;
            }
            inner.IsHitTestVisible = false;
            outer.Child = inner;
            outer.PointerPressed += OnSplitterPressed;
            outer.PointerMoved += OnSplitterMoved;
            outer.PointerReleased += OnSplitterReleased;
            outer.PointerCanceled += OnSplitterReleased;
            outer.PointerCaptureLost += OnSplitterCaptureLost;
            outer.PointerEntered += OnSplitterPointerEntered;
            outer.PointerExited += OnSplitterPointerExited;
            return outer;
        }

        // ---------- 布局 ----------

        private void LayoutAll()
        {
            if (_viewModel == null || PaneHost == null)
            {
                return;
            }
            if (!DispatcherHelper.HasThreadAccess)
            {
                DispatcherHelper.Post(LayoutAll);
                return;
            }
            double width = PaneHost.ActualWidth;
            double height = PaneHost.ActualHeight;
            if (width <= 0 || height <= 0)
            {
                return;
            }
            WorkspaceTab active = _viewModel.ActiveTab;
            if (active == null || active.Tree == null)
            {
                return;
            }
            HashSet<string> visible = VisibleSessionIds(active.Tree, width);
            string focused = active.Tree.FocusedSessionId;
            foreach (KeyValuePair<string, Border> pair in _chrome)
            {
                bool show = visible.Contains(pair.Key);
                pair.Value.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                TerminalView view;
                if (_views.TryGetValue(pair.Key, out view) && view != null)
                {
                    view.SetRenderingActive(show);
                }
                if (show)
                {
                    pair.Value.BorderBrush = pair.Key == focused
                        ? Banner.ResolveThemedBrush("AppAccentBrush")
                        : Banner.ResolveThemedBrush("AppBorderBrush");
                }
            }
            PaneLayout layout = ComputeLayout();
            if (layout == null)
            {
                return;
            }
            if (IsNarrow(width))
            {
                // 窄屏：只显示聚焦叶子占满全幅，窗格树不销毁。
                for (int i = 0; i < layout.Panes.Count; i++)
                {
                    PaneRect rect = layout.Panes[i];
                    if (rect.SessionId != focused)
                    {
                        continue;
                    }
                    PositionChrome(rect.SessionId, 0, 0, width, height);
                }
                return;
            }
            for (int i = 0; i < layout.Panes.Count; i++)
            {
                PaneRect rect = layout.Panes[i];
                PositionChrome(rect.SessionId, rect.X, rect.Y, rect.Width, rect.Height);
            }
            for (int i = 0; i < layout.Splitters.Count; i++)
            {
                SplitterRect rect = layout.Splitters[i];
                Border element;
                if (!_splitters.TryGetValue(rect.Node, out element) || element == null)
                {
                    continue;
                }
                bool vertical = rect.Node.Orientation == SplitOrientation.Column;
                double hit = (double)Application.Current.Resources["SpaceLg"];
                if (vertical)
                {
                    Canvas.SetLeft(element, rect.X + rect.Width / 2 - hit / 2);
                    Canvas.SetTop(element, rect.Y);
                    element.Width = hit;
                    element.Height = Math.Max(0, rect.Height);
                }
                else
                {
                    Canvas.SetLeft(element, rect.X);
                    Canvas.SetTop(element, rect.Y + rect.Height / 2 - hit / 2);
                    element.Width = Math.Max(0, rect.Width);
                    element.Height = hit;
                }
                element.Visibility = Visibility.Visible;
            }
        }

        private void PositionChrome(string sessionId, double x, double y, double width, double height)
        {
            Border frame;
            if (!_chrome.TryGetValue(sessionId, out frame) || frame == null)
            {
                return;
            }
            Canvas.SetLeft(frame, x);
            Canvas.SetTop(frame, y);
            frame.Width = Math.Max(0, width);
            frame.Height = Math.Max(0, height);
        }

        // 窄屏只显示聚焦叶子；布局仍用 ComputeLayout（分隔条宽度可忽略），
        // 实际定位时聚焦叶占满全幅。
        private HashSet<string> VisibleSessionIds(PaneTree tree, double hostWidth)
        {
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            if (IsNarrow(hostWidth))
            {
                ids.Add(tree.FocusedSessionId);
                return ids;
            }
            List<LeafNode> leaves = tree.Leaves();
            for (int i = 0; i < leaves.Count; i++)
            {
                ids.Add(leaves[i].SessionId);
            }
            return ids;
        }

        private PaneLayout ComputeLayout()
        {
            if (_viewModel == null || PaneHost == null)
            {
                return null;
            }
            WorkspaceTab active = _viewModel.ActiveTab;
            if (active == null || active.Tree == null)
            {
                return null;
            }
            double width = PaneHost.ActualWidth;
            double height = PaneHost.ActualHeight;
            if (width <= 0 || height <= 0)
            {
                return null;
            }
            double splitter = (double)Application.Current.Resources["SplitterThickness"];
            return active.Tree.ComputeLayout(0, 0, width, height, splitter);
        }

        private bool IsNarrow(double hostWidth)
        {
            double breakpoint = (double)Application.Current.Resources["WideBreakpoint"];
            return hostWidth < breakpoint;
        }

        private void UpdateEmpty()
        {
            bool hasTab = _viewModel != null && _viewModel.ActiveTab != null;
            Empty.Visibility = hasTab ? Visibility.Collapsed : Visibility.Visible;
        }

        // ---------- 窗格交互 ----------

        private string ChromeSessionId(object sender)
        {
            FrameworkElement element = sender as FrameworkElement;
            return element != null ? element.Tag as string : null;
        }

        private void OnPaneTapped(object sender, TappedRoutedEventArgs e)
        {
            string sessionId = ChromeSessionId(sender);
            if (_viewModel == null || string.IsNullOrEmpty(sessionId))
            {
                return;
            }
            _viewModel.FocusPane(sessionId);
            TerminalView view;
            if (_views.TryGetValue(sessionId, out view) && view != null)
            {
                if (HardwareKeyboardInput.IsHardwareKeyboardPresent)
                {
                    view.IsTabStop = true;
                    view.Focus(FocusState.Programmatic);
                }
                else
                {
                    view.FocusInput();
                }
            }
            e.Handled = true;
        }

        private void OnPaneHolding(object sender, HoldingRoutedEventArgs e)
        {
            if (e.HoldingState != HoldingState.Started)
            {
                return;
            }
            StackPanel bar;
            if (_hoverBars.TryGetValue(ChromeSessionId(sender), out bar) && bar != null)
            {
                bar.Visibility = bar.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            }
            e.Handled = true;
        }

        private void OnPanePointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (e.Pointer == null || !e.Pointer.IsInContact)
            {
                StackPanel bar;
                if (_hoverBars.TryGetValue(ChromeSessionId(sender), out bar) && bar != null)
                {
                    bar.Visibility = Visibility.Visible;
                }
            }
        }

        private void OnPanePointerExited(object sender, PointerRoutedEventArgs e)
        {
            StackPanel bar;
            if (_hoverBars.TryGetValue(ChromeSessionId(sender), out bar) && bar != null)
            {
                bar.Visibility = Visibility.Collapsed;
            }
        }

        private void OnPaneSplitClick(object sender, RoutedEventArgs e)
        {
            string sessionId = ChromeSessionId(sender);
            FrameworkElement anchor = sender as FrameworkElement;
            if (_viewModel == null || string.IsNullOrEmpty(sessionId) || anchor == null)
            {
                return;
            }
            _viewModel.FocusPane(sessionId);
            var flyout = new MenuFlyout();
            var right = new MenuFlyoutItem { Text = "向右分屏" };
            right.Click += (s, args) =>
            {
                var ignore = _viewModel.SplitFocusedPaneAsync(SplitOrientation.Column);
            };
            var down = new MenuFlyoutItem { Text = "向下分屏" };
            down.Click += (s, args) =>
            {
                var ignore = _viewModel.SplitFocusedPaneAsync(SplitOrientation.Row);
            };
            flyout.Items.Add(right);
            flyout.Items.Add(down);
            try
            {
                flyout.ShowAt(anchor);
            }
            catch (Exception)
            {
            }
        }

        private void OnPaneCloseClick(object sender, RoutedEventArgs e)
        {
            string sessionId = ChromeSessionId(sender);
            if (_viewModel != null && !string.IsNullOrEmpty(sessionId))
            {
                _viewModel.ClosePane(sessionId);
            }
        }

        // 快捷键：结构动作走 VM（复用 ShortcutMap 语义与 TerminalModes 键位），
        // 视图局部动作直接操作发出该快捷键的 TerminalView。
        private void OnPaneShortcut(object sender, ShortcutActionEventArgs e)
        {
            if (e == null || _viewModel == null)
            {
                return;
            }
            if (_viewModel.HandleShortcut(e.Action))
            {
                return;
            }
            TerminalView view = sender as TerminalView;
            if (view == null)
            {
                return;
            }
            switch (e.Action)
            {
                case ShortcutAction.Copy:
                    view.CopySelectionToClipboard();
                    break;
                case ShortcutAction.Paste:
                    view.PasteFromClipboard();
                    break;
                case ShortcutAction.FontIncrease:
                    view.Renderer.FontSize = view.Renderer.FontSize + 1;
                    break;
                case ShortcutAction.FontDecrease:
                    view.Renderer.FontSize = view.Renderer.FontSize - 1;
                    break;
                case ShortcutAction.FontReset:
                    try
                    {
                        view.Renderer.FontSize = (float)Defaults.DefaultAppearance().FontSize;
                    }
                    catch (Exception)
                    {
                    }
                    break;
                default:
                    break;
            }
        }

        // ---------- 分隔条拖动 ----------

        private void OnSplitterPressed(object sender, PointerRoutedEventArgs e)
        {
            Border element = sender as Border;
            if (element == null || _dragging)
            {
                return;
            }
            SplitNode node = element.Tag as SplitNode;
            if (node == null)
            {
                return;
            }
            _dragging = true;
            _dragNode = node;
            _dragPointerId = e.Pointer != null ? e.Pointer.PointerId : 0;
            _dragStart = e.GetCurrentPoint(PaneHost).Position;
            _dragStartRatio = node.Ratio;
            try
            {
                element.CapturePointer(e.Pointer);
            }
            catch (Exception)
            {
            }
            e.Handled = true;
        }

        private void OnSplitterMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_dragging || _dragNode == null || _viewModel == null)
            {
                return;
            }
            if (e.Pointer == null || e.Pointer.PointerId != _dragPointerId)
            {
                return;
            }
            Point pos = e.GetCurrentPoint(PaneHost).Position;
            double splitter = (double)Application.Current.Resources["SplitterThickness"];
            bool vertical = _dragNode.Orientation == SplitOrientation.Column;
            double available = (vertical ? PaneHost.ActualWidth : PaneHost.ActualHeight) - splitter;
            if (available <= 0)
            {
                return;
            }
            double delta = vertical ? pos.X - _dragStart.X : pos.Y - _dragStart.Y;
            // SetRatio 内部夹到 [0.15, 0.85]；拖动中只搬位置，释放后各窗格自动 Resize。
            _viewModel.SetPaneRatio(_dragNode, _dragStartRatio + delta / available);
            e.Handled = true;
        }

        private void OnSplitterReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_dragging)
            {
                return;
            }
            Border element = sender as Border;
            if (element != null)
            {
                try
                {
                    element.ReleasePointerCapture(e.Pointer);
                }
                catch (Exception)
                {
                }
            }
            EndDrag();
        }

        private void OnSplitterCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            if (_dragging)
            {
                EndDrag();
            }
        }

        private void EndDrag()
        {
            _dragging = false;
            _dragNode = null;
            _dragPointerId = 0;
            Rebuild();
            FrameScheduler.Instance.Wake();
        }

        private void OnSplitterPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            Border element = sender as Border;
            if (element == null)
            {
                return;
            }
            SplitNode node = element.Tag as SplitNode;
            if (node == null)
            {
                return;
            }
            try
            {
                Window window = Window.Current;
                CoreWindow core = window != null ? window.CoreWindow : null;
                if (core == null)
                {
                    return;
                }
                _savedCursor = core.PointerCursor;
                var cursorType = node.Orientation == SplitOrientation.Column
                    ? CoreCursorType.SizeWestEast
                    : CoreCursorType.SizeNorthSouth;
                core.PointerCursor = new CoreCursor(cursorType, 1);
            }
            catch (Exception)
            {
            }
        }

        private void OnSplitterPointerExited(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                Window window = Window.Current;
                CoreWindow core = window != null ? window.CoreWindow : null;
                if (core == null)
                {
                    return;
                }
                core.PointerCursor = _savedCursor != null
                    ? _savedCursor
                    : new CoreCursor(CoreCursorType.Arrow, 1);
            }
            catch (Exception)
            {
            }
        }

        // ---------- 主机选择器 ----------

        private void OnHostPickerRequested(object sender, EventArgs e)
        {
            if (!DispatcherHelper.HasThreadAccess)
            {
                DispatcherHelper.Post(() => OnHostPickerRequested(sender, e));
                return;
            }
            ShowHostPicker();
        }

        private async void ShowHostPicker()
        {
            if (_viewModel == null || AppServices.Current == null)
            {
                return;
            }
            IReadOnlyList<Core.Models.Host> hosts;
            try
            {
                hosts = await AppServices.Current.Hosts.GetAllAsync().ConfigureAwait(true);
            }
            catch (Exception)
            {
                return;
            }
            var flyout = new MenuFlyout();
            if (hosts == null || hosts.Count == 0)
            {
                var empty = new MenuFlyoutItem { Text = "还没有主机，请先添加", IsEnabled = false };
                flyout.Items.Add(empty);
            }
            else
            {
                for (int i = 0; i < hosts.Count; i++)
                {
                    Core.Models.Host host = hosts[i];
                    if (host == null)
                    {
                        continue;
                    }
                    string hostId = host.Id;
                    var item = new MenuFlyoutItem
                    {
                        Text = string.IsNullOrEmpty(host.Name) ? host.HostName : host.Name
                    };
                    item.Click += (s, args) =>
                    {
                        var ignore = _viewModel.OpenHostInNewTabAsync(hostId);
                    };
                    flyout.Items.Add(item);
                }
            }
            try
            {
                flyout.ShowAt(Strip.AddButtonElement);
            }
            catch (Exception)
            {
            }
        }
    }
}
