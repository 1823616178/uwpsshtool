using System;
using System.Globalization;
using SshTool.Core.Hosts;
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

        private static Brush TryParseColor(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex[0] != '#' || hex.Length != 7)
            {
                return null;
            }
            try
            {
                byte r = byte.Parse(hex.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                byte g = byte.Parse(hex.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                byte b = byte.Parse(hex.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return new SolidColorBrush(Color.FromArgb(255, r, g, b));
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
