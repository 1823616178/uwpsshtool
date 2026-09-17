using Windows.UI.Xaml;
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
        }
    }
}
