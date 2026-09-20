using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Controls
{
    public enum StatusDotState
    {
        Disconnected,
        Connecting,
        Connected,
        Reconnecting,
        Error
    }

    public sealed partial class StatusDot : UserControl
    {
        public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
            nameof(State), typeof(StatusDotState), typeof(StatusDot),
            new PropertyMetadata(StatusDotState.Disconnected, OnStateChanged));

        public StatusDot()
        {
            this.InitializeComponent();
            this.Loaded += (s, e) => UpdateVisual();
        }

        public StatusDotState State
        {
            get { return (StatusDotState)GetValue(StateProperty); }
            set { SetValue(StateProperty, value); }
        }

        private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((StatusDot)d).UpdateVisual();
        }

        private void UpdateVisual()
        {
            VisualStateManager.GoToState(this, State.ToString(), true);
            // UI 走查：状态点纯色彩无文字，屏幕阅读器/放大镜模式读不到状态——补无障碍名。
            // （XAML 中 :24/:41 动画 From/To 字面量保留：CompositionAnimation 无 Token 通道。）
            AutomationProperties.SetName(this, StateNameOf(State));
        }

        private static string StateNameOf(StatusDotState state)
        {
            switch (state)
            {
                case StatusDotState.Connected:
                    return "已连接";
                case StatusDotState.Connecting:
                    return "连接中";
                case StatusDotState.Reconnecting:
                    return "重连中";
                case StatusDotState.Error:
                    return "连接错误";
                default:
                    return "未连接";
            }
        }
    }
}
