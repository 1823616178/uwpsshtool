using System;
using System.Globalization;
using SshTool.Core.Hosts;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media;
using SshTool.Core.Common;

namespace SshTool.App.Controls
{
    public sealed partial class GroupHeader : UserControl
    {
        public GroupHeader()
        {
            this.InitializeComponent();
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
            var group = value as HostListGroup;
            if (group != null)
            {
                return group;
            }
            var cvg = value as ICollectionViewGroup;
            return cvg != null ? cvg.Group as HostListGroup : null;
        }

        private void BindGroup()
        {
            HostListGroup group = Group;
            if (group == null)
            {
                return;
            }
            // V01a：▸/▾ 文本换 MDL2 尖括号（右=E76C 与既有 E70D 下同族）。
            Chevron.Glyph = (string)Application.Current.Resources[group.IsCollapsed ? "IconChevronRight" : "IconChevronDown"];
            TitleText.Text = (group.Name ?? string.Empty) + " (" + group.HostCount.ToString() + ")";
            Brush brush = TryParseColor(group.Color);
            if (brush != null)
            {
                ColorSwatch.Background = brush;
                ColorSwatch.Visibility = Visibility.Visible;
            }
            else
            {
                ColorSwatch.Visibility = Visibility.Collapsed;
            }
        }

        // O14：解析收口到 Core 的 ColorHex（此前三处逐字重复）。
        private static Brush TryParseColor(string hex)
        {
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
