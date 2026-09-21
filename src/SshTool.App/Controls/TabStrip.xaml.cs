using System;
using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using SshTool.App.ViewModels;
using SshTool.Core.Common;
using SshTool.Core.Terminal;
using Windows.ApplicationModel.Resources;
using Windows.UI.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace SshTool.App.Controls
{
    // U12：宽屏标签栏。新建/关闭/右键菜单（重命名、复制会话、向右/向下分屏、关闭其他）。
    public sealed partial class TabStrip : UserControl
    {
        private WorkspaceViewModel _viewModel;

        public TabStrip()
        {
            this.InitializeComponent();
            this.Unloaded += OnUnloaded;
        }

        // 主机选择器锚点（TerminalWorkspace 在此弹出主机 MenuFlyout）。
        public FrameworkElement AddButtonElement
        {
            get { return AddButton; }
        }

        public WorkspaceViewModel ViewModel
        {
            get { return _viewModel; }
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
            }
            _viewModel = viewModel;
            if (_viewModel != null)
            {
                _viewModel.WorkspaceChanged += OnWorkspaceChanged;
            }
            Rebuild();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_viewModel != null)
            {
                _viewModel.WorkspaceChanged -= OnWorkspaceChanged;
                _viewModel = null;
            }
        }

        private void OnWorkspaceChanged(object sender, EventArgs e)
        {
            if (!DispatcherHelper.HasThreadAccess)
            {
                DispatcherHelper.Post(() => OnWorkspaceChanged(sender, e));
                return;
            }
            Rebuild();
        }

        private void OnAddClick(object sender, RoutedEventArgs e)
        {
            if (_viewModel != null)
            {
                _viewModel.RequestNewTab();
            }
        }

        private void Rebuild()
        {
            TabsPanel.Children.Clear();
            if (_viewModel == null)
            {
                return;
            }
            string activeId = _viewModel.ActiveTabId;
            for (int i = 0; i < _viewModel.Tabs.Count; i++)
            {
                WorkspaceTab tab = _viewModel.Tabs[i];
                TabsPanel.Children.Add(BuildTabItem(tab, tab.TabId == activeId));
            }
        }

        private FrameworkElement BuildTabItem(WorkspaceTab tab, bool active)
        {
            var outer = new Border();
            outer.Tag = tab.TabId;
            outer.BorderThickness = (Thickness)Application.Current.Resources["BorderThinBottom"];
            outer.BorderBrush = active
                ? Banner.ResolveThemedBrush("AppAccentBrush")
                : new SolidColorBrush(Windows.UI.Colors.Transparent);
            outer.Background = active
                ? Banner.ResolveThemedBrush("AppSurfaceAltBrush")
                : new SolidColorBrush(Windows.UI.Colors.Transparent);

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var title = new TextBlock();
            title.Text = string.IsNullOrEmpty(tab.Title) ? tab.TabId : tab.Title;
            title.Style = (Style)Application.Current.Resources["BodyTextStyle"];
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            title.VerticalAlignment = VerticalAlignment.Center;
            title.Margin = (Thickness)Application.Current.Resources["PagePadding"];
            title.Tag = tab.TabId;
            title.Tapped += OnTabTapped;
            row.Children.Add(title);

            var close = new Button();
            // C-03：TerminalIconButtonStyle 统一焦点行为（AllowFocusOnInteraction=False）与
            // 触控宽（§7.5）；高度受 TabStripHeight=36 行约束；无障碍名走 resw（C# 创建用 ResourceLoader）。
            close.Style = (Style)Application.Current.Resources["TerminalIconButtonStyle"];
            close.Content = new FontIcon
            {
                Glyph = (string)Application.Current.Resources["IconClose"]
            };
            string closeA11yName = ResourceLoader.GetForCurrentView().GetString("TabStrip_CloseButton_A11yName");
            if (!string.IsNullOrEmpty(closeA11yName))
            {
                Windows.UI.Xaml.Automation.AutomationProperties.SetName(close, closeA11yName);
            }
            close.Tag = tab.TabId;
            close.Height = (double)Application.Current.Resources["TabStripHeight"];
            // C-03：Transparent 是有意覆盖样式的 Surface 底——✕ 直接坐在标签行上，不带自己的底色。
            close.Background = new SolidColorBrush(Windows.UI.Colors.Transparent);
            close.Foreground = Banner.ResolveThemedBrush("AppTextDimBrush");
            close.Click += OnCloseClick;
            Grid.SetColumn(close, 1);
            row.Children.Add(close);

            outer.Child = row;
            outer.Tag = tab.TabId;
            outer.Tapped += OnTabTapped;
            outer.RightTapped += OnTabRightTapped;
            outer.Holding += OnTabHolding;
            return outer;
        }

        private string TabIdOf(object sender)
        {
            FrameworkElement element = sender as FrameworkElement;
            return element != null ? element.Tag as string : null;
        }

        private void OnTabTapped(object sender, TappedRoutedEventArgs e)
        {
            if (_viewModel != null)
            {
                _viewModel.ActivateTab(TabIdOf(sender));
            }
            e.Handled = true;
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            if (_viewModel != null)
            {
                _viewModel.CloseTab(TabIdOf(sender));
            }
        }

        private void OnTabRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            ShowTabMenu(sender as FrameworkElement, TabIdOf(sender));
            e.Handled = true;
        }

        private void OnTabHolding(object sender, HoldingRoutedEventArgs e)
        {
            if (e.HoldingState != HoldingState.Started)
            {
                return;
            }
            ShowTabMenu(sender as FrameworkElement, TabIdOf(sender));
            e.Handled = true;
        }

        // §5.7 标签右键：重命名、复制会话、向右分屏、向下分屏、关闭其他（+关闭）。
        private void ShowTabMenu(FrameworkElement anchor, string tabId)
        {
            if (_viewModel == null || string.IsNullOrEmpty(tabId) || anchor == null)
            {
                return;
            }
            var flyout = new MenuFlyout();
            flyout.Items.Add(MenuItem("重命名", () => RenameAsync(tabId)));
            flyout.Items.Add(MenuItem("复制会话", () =>
            {
                _viewModel.DuplicateFocusedSessionAsync(tabId).Forget("TabStrip.DuplicateSession", AppLog.Logger);
            }));
            flyout.Items.Add(MenuItem("向右分屏", () =>
            {
                _viewModel.SplitTabPaneAsync(tabId, SplitOrientation.Column).Forget("TabStrip.SplitPane", AppLog.Logger);
            }));
            flyout.Items.Add(MenuItem("向下分屏", () =>
            {
                _viewModel.SplitTabPaneAsync(tabId, SplitOrientation.Row).Forget("TabStrip.SplitPane", AppLog.Logger);
            }));
            flyout.Items.Add(MenuItem("关闭其他", () => _viewModel.CloseOthers(tabId)));
            flyout.Items.Add(MenuItem("关闭", () => _viewModel.CloseTab(tabId)));
            try
            {
                flyout.ShowAt(anchor);
            }
            catch (Exception)
            {
            }
        }

        private static MenuFlyoutItem MenuItem(string text, Action action)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (s, e) => action();
            return item;
        }

        private async void RenameAsync(string tabId)
        {
            if (_viewModel == null)
            {
                return;
            }
            WorkspaceTab tab = _viewModel.FindTab(tabId);
            if (tab == null)
            {
                return;
            }
            var box = new TextBox { Text = tab.Title ?? string.Empty };
            var dialog = new ContentDialog
            {
                Title = "重命名标签",
                Content = box,
                PrimaryButtonText = "确定",
                SecondaryButtonText = "取消",
                MaxWidth = (double)Application.Current.Resources["DialogMaxWidth"]
            };
            ContentDialogResult result;
            try
            {
                result = await ServiceRegistry.Get<DialogService>().ShowAsync(dialog).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AppLog.Error("TabStrip", "重命名对话框失败", ex);
                return;
            }
            if (result == ContentDialogResult.Primary)
            {
                _viewModel.RenameTab(tabId, box.Text);
            }
        }
    }
}
