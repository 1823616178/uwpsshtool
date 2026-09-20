using System;
using System.Windows.Input;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;

namespace SshTool.App.Controls
{
    public sealed partial class BottomActionBar : UserControl
    {
        public static readonly DependencyProperty PrimaryTextProperty = DependencyProperty.Register(
            nameof(PrimaryText), typeof(string), typeof(BottomActionBar),
            new PropertyMetadata(string.Empty, OnChanged));
        public static readonly DependencyProperty PrimaryCommandProperty = DependencyProperty.Register(
            nameof(PrimaryCommand), typeof(ICommand), typeof(BottomActionBar),
            new PropertyMetadata(null));
        public static readonly DependencyProperty ShowOverflowProperty = DependencyProperty.Register(
            nameof(ShowOverflow), typeof(bool), typeof(BottomActionBar),
            new PropertyMetadata(false, OnChanged));
        public static readonly DependencyProperty OverflowFlyoutProperty = DependencyProperty.Register(
            nameof(OverflowFlyout), typeof(FlyoutBase), typeof(BottomActionBar),
            new PropertyMetadata(null, OnChanged));
        public static readonly DependencyProperty IsKeyboardVisibleProperty = DependencyProperty.Register(
            nameof(IsKeyboardVisible), typeof(bool), typeof(BottomActionBar),
            new PropertyMetadata(false, OnChanged));

        private InputPane _inputPane;

        public event EventHandler PrimaryClick;
        public event EventHandler OverflowClick;

        public BottomActionBar()
        {
            this.InitializeComponent();
            this.Loaded += OnLoaded;
            this.Unloaded += OnUnloaded;
        }

        public string PrimaryText
        {
            get { return (string)GetValue(PrimaryTextProperty); }
            set { SetValue(PrimaryTextProperty, value); }
        }

        public ICommand PrimaryCommand
        {
            get { return (ICommand)GetValue(PrimaryCommandProperty); }
            set { SetValue(PrimaryCommandProperty, value); }
        }

        public bool ShowOverflow
        {
            get { return (bool)GetValue(ShowOverflowProperty); }
            set { SetValue(ShowOverflowProperty, value); }
        }

        // 溢出按钮的菜单；ShowOverflow=true 时点击即弹出（Button.Flyout）。
        public FlyoutBase OverflowFlyout
        {
            get { return (FlyoutBase)GetValue(OverflowFlyoutProperty); }
            set { SetValue(OverflowFlyoutProperty, value); }
        }

        // keyboard-visible 态：true 时整条收起让位。控件自身按 InputPane 更新，
        // 页面也可直接设置（桌面无 SIP 的画廊/预览场景）。
        public bool IsKeyboardVisible
        {
            get { return (bool)GetValue(IsKeyboardVisibleProperty); }
            set { SetValue(IsKeyboardVisibleProperty, value); }
        }

        private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((BottomActionBar)d).UpdateVisual();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            UpdateVisual();
            AttachInputPane();
        }

        // Owner 语义（评审回补）：InputPane 订阅只靠 Unloaded 解除——与 SoftKeyboardInput
        // 同一模式，但后者另有页面 Dispose 兜底（TerminalView 持有并显式释放），本控件没有。
        // 控件生命周期即所在页面/视图；InputPane 是每视图单例，极端情况下（控件未经历
        // Unloaded 即被弃置）残留订阅也随视图销毁而终结，不会跨页面悬挂。
        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            DetachInputPane();
        }

        // SIP 感知与 SoftKeyboardInput 同一机制：InputPane.Showing/Hiding +
        // OccludedRect.Height 代理（桌面窗口化模式 OS 可能改窗口大小而非遮挡，
        // Height 为 0 视为未弹出；W10M 永远遮挡，真机行为一致）。
        private void AttachInputPane()
        {
            DetachInputPane();
            try
            {
                _inputPane = InputPane.GetForCurrentView();
            }
            catch (Exception)
            {
                _inputPane = null;
            }
            if (_inputPane != null)
            {
                _inputPane.Showing += OnInputPaneShowing;
                _inputPane.Hiding += OnInputPaneHiding;
            }
        }

        private void DetachInputPane()
        {
            if (_inputPane == null)
            {
                return;
            }
            _inputPane.Showing -= OnInputPaneShowing;
            _inputPane.Hiding -= OnInputPaneHiding;
            _inputPane = null;
        }

        private void OnInputPaneShowing(InputPane sender, InputPaneVisibilityEventArgs args)
        {
            IsKeyboardVisible = args.OccludedRect.Height > 0;
        }

        private void OnInputPaneHiding(InputPane sender, InputPaneVisibilityEventArgs args)
        {
            IsKeyboardVisible = false;
        }

        private void UpdateVisual()
        {
            bool hasPrimary = !string.IsNullOrEmpty(PrimaryText);
            PrimaryButton.Content = PrimaryText ?? string.Empty;
            PrimaryButton.Visibility = hasPrimary ? Visibility.Visible : Visibility.Collapsed;

            OverflowButton.Visibility = ShowOverflow ? Visibility.Visible : Visibility.Collapsed;
            OverflowButton.Flyout = OverflowFlyout;

            // 收起只动内部 Root，不碰控件自身 Visibility（页面隐藏语义不受键盘状态干扰）。
            Root.Visibility = IsKeyboardVisible ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnPrimaryClick(object sender, RoutedEventArgs e)
        {
            // 事件先于 command 触发；消费方只接其一，避免双执行（同 Banner.ActionClick）。
            EventHandler handler = PrimaryClick;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
            if (PrimaryCommand != null && PrimaryCommand.CanExecute(null))
            {
                PrimaryCommand.Execute(null);
            }
        }

        private void OnOverflowClick(object sender, RoutedEventArgs e)
        {
            EventHandler handler = OverflowClick;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }
    }
}
