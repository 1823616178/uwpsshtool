using System;
using SshTool.Core.Common;
using Windows.Foundation;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace SshTool.App.Infrastructure
{
    // ui/fix-pass：表单页软键盘避让基座。用法：<ScrollViewer infra:InputPaneScroll.IsEnabled="True">。
    // W10M 默认在 SIP 弹出时把整窗上推（页头被推出屏幕），且 ScrollViewer 的可滚范围不含被盖住的部分，
    // 底部输入框滚不出来。这里接管：SIP 弹出时给滚动区底部补上被盖高度的内边距，
    // 并把焦点输入框滚到未遮挡区域；切换焦点（下一项）时再算一次；SIP 收起还原。
    public sealed class InputPaneScroll
    {
        public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(InputPaneScroll), new PropertyMetadata(false, OnIsEnabledChanged));

        private static readonly DependencyProperty BehaviorProperty = DependencyProperty.RegisterAttached(
            "Behavior", typeof(InputPaneScroll), typeof(InputPaneScroll), new PropertyMetadata(null));

        public static bool GetIsEnabled(DependencyObject element)
        {
            return (bool)element.GetValue(IsEnabledProperty);
        }

        public static void SetIsEnabled(DependencyObject element, bool value)
        {
            element.SetValue(IsEnabledProperty, value);
        }

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var viewer = d as ScrollViewer;
            if (viewer == null || !(bool)e.NewValue || viewer.GetValue(BehaviorProperty) != null)
            {
                return;
            }
            viewer.SetValue(BehaviorProperty, new InputPaneScroll(viewer));
        }

        private readonly ScrollViewer _viewer;
        private InputPane _pane;
        private Thickness _basePadding;
        private double _occluded;
        private double _overlap;

        private InputPaneScroll(ScrollViewer viewer)
        {
            _viewer = viewer;
            _viewer.Loaded += OnLoaded;
            _viewer.Unloaded += OnUnloaded;
            _viewer.GotFocus += OnGotFocus;
            _viewer.SizeChanged += OnSizeChanged;
        }

        // SIP 弹出时页面底栏（BottomActionBar）会收起让位，滚动区随之变高——重算被盖高度。
        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_occluded > 0)
            {
                ApplyPadding();
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Detach();
            try
            {
                _pane = InputPane.GetForCurrentView();
            }
            catch (Exception)
            {
                _pane = null;
            }
            if (_pane != null)
            {
                _pane.Showing += OnShowing;
                _pane.Hiding += OnHiding;
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            Detach();
            Restore();
        }

        private void Detach()
        {
            if (_pane == null)
            {
                return;
            }
            _pane.Showing -= OnShowing;
            _pane.Hiding -= OnHiding;
            _pane = null;
        }

        private void OnShowing(InputPane sender, InputPaneVisibilityEventArgs args)
        {
            double occluded = args.OccludedRect.Height;
            if (occluded <= 0 || !ContainsFocus())
            {
                return;
            }
            // 由本类负责把焦点元素滚进可见区，系统不再整窗上推。
            args.EnsuredFocusedElementInView = true;
            if (_occluded <= 0)
            {
                _basePadding = _viewer.Padding;
            }
            _occluded = occluded;
            ApplyPadding();
            DispatcherHelper.Post(BringFocusedIntoView);
        }

        private void OnHiding(InputPane sender, InputPaneVisibilityEventArgs args)
        {
            Restore();
        }

        private void OnGotFocus(object sender, RoutedEventArgs e)
        {
            if (_occluded > 0)
            {
                DispatcherHelper.Post(BringFocusedIntoView);
            }
        }

        private void Restore()
        {
            if (_occluded <= 0)
            {
                return;
            }
            _occluded = 0;
            _overlap = 0;
            _viewer.Padding = _basePadding;
        }

        private void ApplyPadding()
        {
            var root = Window.Current != null ? Window.Current.Content as UIElement : null;
            if (root == null)
            {
                return;
            }
            Point origin = _viewer.TransformToVisual(root).TransformPoint(new Point(0, 0));
            double bottom = origin.Y + _viewer.ActualHeight;
            _overlap = InputPaneMath.Overlap(bottom, Window.Current.Bounds.Height, _occluded);
            _viewer.Padding = new Thickness(_basePadding.Left, _basePadding.Top, _basePadding.Right,
                _basePadding.Bottom + _overlap);
        }

        private void BringFocusedIntoView()
        {
            if (_occluded <= 0)
            {
                return;
            }
            var focused = FocusManager.GetFocusedElement() as FrameworkElement;
            if (focused == null || !IsDescendant(focused))
            {
                return;
            }
            Point top = focused.TransformToVisual(_viewer).TransformPoint(new Point(0, 0));
            double margin = TokenDouble("SpaceLg");
            double? target = InputPaneMath.ScrollTarget(_viewer.VerticalOffset, top.Y, focused.ActualHeight,
                VisibleHeight(), margin);
            if (target.HasValue)
            {
                _viewer.ChangeView(null, target.Value, null, false);
            }
        }

        // fix/functional-pass（P2-8）：按几何算未遮挡高度（旧实现 ViewportHeight − overlap 在 Padding
        // 生效后重复扣减，见 InputPaneMath.VisibleHeight）。
        private double VisibleHeight()
        {
            var root = Window.Current != null ? Window.Current.Content as UIElement : null;
            if (root == null)
            {
                return _viewer.ViewportHeight;
            }
            Point origin = _viewer.TransformToVisual(root).TransformPoint(new Point(0, 0));
            return InputPaneMath.VisibleHeight(origin.Y, _viewer.ActualHeight, _viewer.Padding.Bottom,
                Window.Current.Bounds.Height, _occluded);
        }

        private bool ContainsFocus()
        {
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            return focused != null && IsDescendant(focused);
        }

        private bool IsDescendant(DependencyObject element)
        {
            DependencyObject current = element;
            while (current != null)
            {
                if (ReferenceEquals(current, _viewer))
                {
                    return true;
                }
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        private static double TokenDouble(string key)
        {
            object value;
            if (Application.Current.Resources.TryGetValue(key, out value) && value is double)
            {
                return (double)value;
            }
            return 0;
        }
    }
}
