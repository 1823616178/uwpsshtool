using System;
using System.Collections;
using System.Collections.Generic;
using SshTool.App.Infrastructure;
using SshTool.App.Platform;
using Windows.ApplicationModel.Resources;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
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

        // 质量评审 Important-1：段 Button 内容层不透明 fill 盖住默认模板 Root，模板
        // PointerOver/Pressed 视觉不可见，改为手动调 fill 不透明度；选中/未选配色不受影响。
        private const double SegmentHoverOpacity = 0.85;
        private const double SegmentPressedOpacity = 0.6;
        private Button _pressedSegment;

        public event EventHandler SelectionChanged;

        public SegmentTabs()
        {
            this.InitializeComponent();
            // ui/fix-pass：代码赋值的主题画刷随 ThemeService.ThemeChanged 重算。
            ThemeRefreshHook.Attach(this, UpdateVisual);
            // 质量评审 Minor-5：去掉 Loaded→Rebuild 与 ItemsSource DP 回调的重复构建，只留后者。
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

            double segHeight = (double)Application.Current.Resources["SegmentHeight"];
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
                // Button 内部会把指针事件标记 handled，须 handledEventsToo: true 才收得到。
                button.AddHandler(PointerPressedEvent, new PointerEventHandler(OnSegmentPointerPressed), true);
                button.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnSegmentPointerReleased), true);
                button.AddHandler(PointerEnteredEvent, new PointerEventHandler(OnSegmentPointerEntered), true);
                button.AddHandler(PointerExitedEvent, new PointerEventHandler(OnSegmentPointerExited), true);
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

        private void OnSegmentPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (_pressedSegment == null)
            {
                ((Border)((Button)sender).Content).Opacity = SegmentHoverOpacity;
            }
        }

        private void OnSegmentPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _pressedSegment = (Button)sender;
            ((Border)_pressedSegment.Content).Opacity = SegmentPressedOpacity;
        }

        private void OnSegmentPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _pressedSegment = null;
            // 松手时指针通常仍在段内，回到悬停态；若已移出，随后的 PointerExited 复原。
            ((Border)((Button)sender).Content).Opacity = SegmentHoverOpacity;
        }

        private void OnSegmentPointerExited(object sender, PointerRoutedEventArgs e)
        {
            _pressedSegment = null;
            ((Border)((Button)sender).Content).Opacity = 1.0;
        }

        private void UpdateVisual()
        {
            Brush accent = ThemeService.ResolveBrush("AppAccentBrush");
            Brush onAccent = ThemeService.ResolveBrush("AppOnAccentBrush");
            Brush surface = ThemeService.ResolveBrush("AppSurfaceBrush");
            Brush text = ThemeService.ResolveBrush("AppTextBrush");
            // 质量评审 Important-2：选中态进无障碍树——读屏播报「标签，已选」，未选段只报标签。
            string selectedSuffix = ResourceLoader.GetForCurrentView().GetString("SegmentTabs_SelectedSuffix");
            for (int i = 0; i < _segments.Count; i++)
            {
                bool selected = i == SelectedIndex;
                var fill = (Border)_segments[i].Content;
                fill.Background = selected ? accent : surface;
                var label = (TextBlock)fill.Child;
                label.Foreground = selected ? onAccent : text;
                AutomationProperties.SetName(_segments[i], selected ? label.Text + selectedSuffix : label.Text);
            }
            Container.Opacity = IsEnabled ? 1.0 : (double)Application.Current.Resources["DisabledOpacity"];
        }
    }
}
