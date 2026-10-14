using System;
using System.Globalization;
using SshTool.App.Infrastructure;
using SshTool.App.Platform;
using SshTool.Core.Common;
using SshTool.Core.Hosts;
using SshTool.App.ViewModels.Snippets;
using Windows.UI;
using Windows.UI.Xaml;
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
            }
        }

        private void BindHostGroup(HostListGroup group)
        {
            // V01a：▸/▾ 文本换 MDL2 尖括号（右=E76C 与既有 E70D 下同族）。
            Chevron.Glyph = (string)Application.Current.Resources[group.IsCollapsed ? "IconChevronRight" : "IconChevronDown"];
            TitleText.Text = Format(group.Name, group.HostCount);

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
            Chevron.Glyph = (string)Application.Current.Resources["IconChevronDown"];
            TitleText.Text = Format(group.Title, group.Rows.Count);
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
