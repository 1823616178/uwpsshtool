using System;
using System.Collections;
using System.Collections.Generic;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace SshTool.App.Controls
{
    public sealed partial class SegmentTabs : UserControl
    {
        public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
            nameof(ItemsSource), typeof(IEnumerable), typeof(SegmentTabs),
            new PropertyMetadata(null, OnItemsChanged));
        public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
            nameof(SelectedIndex), typeof(int), typeof(SegmentTabs),
            new PropertyMetadata(0, OnSelectionChanged));

        private readonly List<Button> _segments = new List<Button>();

        public event EventHandler SelectionChanged;

        public SegmentTabs()
        {
            this.InitializeComponent();
            this.Loaded += (s, e) => Rebuild();
            this.IsEnabledChanged += (s, e) => UpdateVisual();
        }

        // 快照消费：赋值时重建；运行期对源集合增删不追踪（本批消费方都是静态短列表）。
        public IEnumerable ItemsSource
        {
            get { return (IEnumerable)GetValue(ItemsSourceProperty); }
            set { SetValue(ItemsSourceProperty, value); }
        }

        public int SelectedIndex
        {
            get { return (int)GetValue(SelectedIndexProperty); }
            set { SetValue(SelectedIndexProperty, value); }
        }

        private static void OnItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((SegmentTabs)d).Rebuild();
        }

        // V01a 评审回补：代码设置 SelectedIndex 只刷新视觉（UpdateVisual），不触发 SelectionChanged；
        // 事件仅由用户点击段（OnSegmentClick）触发——消费方据此区分「用户切换」与「程序化同步」。
        private static void OnSelectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((SegmentTabs)d).UpdateVisual();
        }

        private void Rebuild()
        {
            var items = new List<string>();
            IEnumerable source = ItemsSource;
            if (source != null)
            {
                foreach (object item in source)
                {
                    items.Add(item == null ? string.Empty : item.ToString());
                }
            }

            SegmentsHost.Children.Clear();
            SegmentsHost.ColumnDefinitions.Clear();
            _segments.Clear();

            double segHeight = (double)Application.Current.Resources["SegmentTabsHeight"];
            Style textStyle = (Style)Application.Current.Resources["BodyTextStyle"];
            CornerRadius radius = (CornerRadius)Application.Current.Resources["RadiusSm"];
            Thickness noPad = (Thickness)Application.Current.Resources["PadNone"];
            Thickness noBorder = (Thickness)Application.Current.Resources["BorderNone"];
            var transparent = new SolidColorBrush(Colors.Transparent);

            for (int i = 0; i < items.Count; i++)
            {
                SegmentsHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var label = new TextBlock
                {
                    Style = textStyle,
                    Text = items[i],
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                // Button 在 15063 无 CornerRadius，圆角选中块画在内容层 Border 上。
                var fill = new Border
                {
                    CornerRadius = radius,
                    Child = label,
                    MinHeight = segHeight
                };
                var button = new Button
                {
                    Content = fill,
                    Padding = noPad,
                    BorderThickness = noBorder,
                    Background = transparent,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    VerticalContentAlignment = VerticalAlignment.Stretch,
                    Tag = i
                };
                button.Click += OnSegmentClick;
                Grid.SetColumn(button, i);
                SegmentsHost.Children.Add(button);
                _segments.Add(button);
            }

            if (_segments.Count > 0 && (SelectedIndex < 0 || SelectedIndex >= _segments.Count))
            {
                SelectedIndex = 0;
            }
            UpdateVisual();
        }

        private void OnSegmentClick(object sender, RoutedEventArgs e)
        {
            int index = (int)((FrameworkElement)sender).Tag;
            if (index == SelectedIndex)
            {
                return;
            }
            SelectedIndex = index;
            EventHandler handler = SelectionChanged;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void UpdateVisual()
        {
            Brush accent = Banner.ResolveThemedBrush("AppAccentBrush");
            Brush onAccent = Banner.ResolveThemedBrush("AppOnAccentBrush");
            Brush surface = Banner.ResolveThemedBrush("AppSurfaceBrush");
            Brush text = Banner.ResolveThemedBrush("AppTextBrush");
            for (int i = 0; i < _segments.Count; i++)
            {
                bool selected = i == SelectedIndex;
                var fill = (Border)_segments[i].Content;
                fill.Background = selected ? accent : surface;
                var label = (TextBlock)fill.Child;
                label.Foreground = selected ? onAccent : text;
            }
            Container.Opacity = IsEnabled ? 1.0 : (double)Application.Current.Resources["DisabledOpacity"];
        }
    }
}
