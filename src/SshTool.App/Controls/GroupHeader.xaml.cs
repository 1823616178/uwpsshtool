using System;
using System.Globalization;
using SshTool.App.Infrastructure;
using SshTool.App.Platform;
using SshTool.Core.Common;
using SshTool.Core.Hosts;
using SshTool.App.ViewModels.Snippets;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media;

namespace SshTool.App.Controls
{
    public sealed partial class GroupHeader : UserControl
    {
        public GroupHeader()
        {
            this.InitializeComponent();
            // ui/fix-pass：代码赋值的主题画刷随 ThemeService.ThemeChanged 重算。
            ThemeRefreshHook.Attach(this, BindGroup);
            this.DataContextChanged += OnDataContextChanged;
        }

        public HostListGroup Group { get; private set; }

        // 分组是否可点折叠。两种数据源都可折叠：主机分组折叠态记忆在设置里，片段分组是
        // 本地视图态（SnippetGroupVm.IsCollapsed）。不可折叠时隐藏尖括号——它是个横向
        // 布局槽，留着会让标题比可折叠的分组多缩进一格。
        public bool CanToggle { get; private set; }

        // 折叠请求（点击分组头）：canToggle=false 时用户点不动，实现方也可据此忽略。
        public event EventHandler ToggleRequested;

        // 折叠态只由冒泡到分组头根部的 PointerReleased 翻转：卡片行的 Tapped 也在冒泡，
        // 用 e.Handled 无法阻止父级调用（Handled 只压制默认动作，不压制已注册的处理器），
        // 所以点击行时由行的 OnTapped 置 SuppressToggle 再自行处理。
        private bool SuppressToggle { get; set; }

        private void OnRootPointerReleased(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (SuppressToggle)
            {
                SuppressToggle = false;
                return;
            }
            RequestToggle();
        }

        // 行点击（向卡片行按钮/连接的点击）优先于折叠：宿主行控件置位后返回 true。
        public bool ConsumeToggle()
        {
            bool pending = SuppressToggle;
            SuppressToggle = true;
            return !pending;
        }

        private void RequestToggle()
        {
            if (!CanToggle)
            {
                return;
            }
            EventHandler handler = ToggleRequested;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            Group = Resolve(args.NewValue);
            BindGroup();
        }

        private static HostListGroup Resolve(object value)
        {
            object source = Unwrap(value);
            return source as HostListGroup;
        }

        // 分组头的两种数据源都是 CollectionViewGroup：主机分组是 HostListGroup
        // （带分组色与折叠态），片段分组是 SnippetGroupVm（分组名 + 行数）。
        private static object Unwrap(object value)
        {
            var cvg = value as ICollectionViewGroup;
            return cvg != null ? cvg.Group : value;
        }

        private void BindGroup()
        {
            object source = Unwrap(DataContext);
            var hostGroup = source as HostListGroup;
            if (hostGroup != null)
            {
                BindHostGroup(hostGroup);
                return;
            }

            var snippetGroup = source as SnippetGroupVm;
            if (snippetGroup != null)
            {
                BindSnippetGroup(snippetGroup);
                return;
            }

            // 列表滚动回收时会把 DataContext 清空：保留上一组的折叠能力，避免视觉抖动。
            ShowGroupChrome(CanToggle);
        }

        private void BindHostGroup(HostListGroup group)
        {
            // V01a：▸/▾ 文本换 MDL2 尖括号（右=E76C 与既有 E70D 下同族）。
            CanToggle = group.IsCollapsed || group.Rows.Count > 0;
            TitleText.Text = Format(group.Name, group.HostCount);
            ShowGroupChrome(CanToggle);
            Chevron.Glyph = (string)Application.Current.Resources[
                CanToggle && group.IsCollapsed ? "IconChevronRight" : "IconChevronDown"];

            if (string.Equals(group.GroupId, HostListGroup.FavoritesId, StringComparison.Ordinal))
            {
                ShowSectionIcon("IconFavoriteStarFill", "AppWarningBrush");
            }
            else if (string.Equals(group.GroupId, HostListGroup.RecentId, StringComparison.Ordinal))
            {
                ShowSectionIcon("IconHistory", "AppAccentBrush");
            }
            else
            {
                ShowColorDot(group.Color);
            }
        }

        private void BindSnippetGroup(SnippetGroupVm group)
        {
            CanToggle = group.Rows.Count > 0;
            TitleText.Text = Format(group.Title, group.Rows.Count);
            ShowGroupChrome(CanToggle);
            Chevron.Glyph = (string)Application.Current.Resources[
                CanToggle && group.IsCollapsed ? "IconChevronRight" : "IconChevronDown"];
            // 未分组用中性底，真实分组用与主机分组头一致的强调色图标：只区分「是不是分组」，
            // 不假装它有分组色（片段分组暂时没有颜色属性）。
            if (string.IsNullOrWhiteSpace(group.GroupName))
            {
                ShowColorDot(null);
            }
            else
            {
                ShowSectionIcon("IconFolder", "AppAccentBrush");
            }
        }

        // 折叠/可点语义只挂在根部：只有可折叠时才进无障碍树并报「可折叠」。
        private void ShowGroupChrome(bool canToggle)
        {
            Chevron.Visibility = canToggle ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetAccessibilityView(this,
                canToggle ? AccessibilityView.Content : AccessibilityView.Raw);
        }

        private static string Format(string name, int count)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0} ({1})", name ?? string.Empty, count);
        }

        private void ShowSectionIcon(string glyphKey, string brushKey)
        {
            ColorDot.Visibility = Visibility.Collapsed;
            SectionIcon.Glyph = (string)Application.Current.Resources[glyphKey];
            SectionIcon.Foreground = ThemeService.ResolveBrush(brushKey);
            SectionIcon.Visibility = Visibility.Visible;
        }

        private void ShowColorDot(string hex)
        {
            SectionIcon.Visibility = Visibility.Collapsed;
            Brush brush = TryParseColor(hex);
            if (brush != null)
            {
                ColorDot.Background = brush;
                ColorDot.Visibility = Visibility.Visible;
            }
            else
            {
                ColorDot.Visibility = Visibility.Collapsed;
            }
        }

        // O14：解析收口到 Core 的 ColorHex（此前三处逐字重复）。
        private static Brush TryParseColor(string hex)
        {
            if (string.IsNullOrEmpty(hex))
            {
                return null;
            }
            byte r;
            byte g;
            byte b;
            if (!ColorHex.TryParse(hex, out r, out g, out b))
            {
                return null;
            }
            return new SolidColorBrush(Color.FromArgb(255, r, g, b));
        }
    }
}
