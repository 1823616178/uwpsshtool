using System;
using SshTool.Core.Sessions;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Animation;

namespace SshTool.App.Controls
{
    public sealed partial class SessionOverlay : UserControl
    {
        public SessionOverlay()
        {
            this.InitializeComponent();
        }

        public event EventHandler Cancel;
        public event EventHandler ReconnectNow;
        public event EventHandler StopReconnect;
        public event EventHandler Retry;
        public event EventHandler EditHost;
        public event EventHandler CloseSession;

        public void Apply(OverlayModel model)
        {
            if (model == null || model.Kind == OverlayKind.None)
            {
                Fade(false);
                return;
            }
            Fade(true);
            Ring.IsActive = model.Kind == OverlayKind.Connecting || model.Kind == OverlayKind.Reconnecting;
            MessageText.Text = model.MessageKey ?? string.Empty;
            CancelButton.Visibility = Vis(model.ShowCancel);
            ReconnectNowButton.Visibility = Vis(model.ShowReconnectNow);
            StopButton.Visibility = Vis(model.ShowStop);
            RetryButton.Visibility = Vis(model.ShowRetry);
            EditButton.Visibility = Vis(model.ShowEditHost);
            CloseButton.Visibility = Vis(model.ShowClose);
        }

        public void SetMessage(string text)
        {
            MessageText.Text = text ?? string.Empty;
        }

        private void Fade(bool show)
        {
            Root.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            var anim = new DoubleAnimation
            {
                To = show ? 1 : 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(150))
            };
            Storyboard.SetTarget(anim, Root);
            Storyboard.SetTargetProperty(anim, "Opacity");
            var sb = new Storyboard();
            sb.Children.Add(anim);
            sb.Begin();
        }

        private static Visibility Vis(bool on)
        {
            return on ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            Raise(Cancel);
        }

        private void OnReconnectNow(object sender, RoutedEventArgs e)
        {
            Raise(ReconnectNow);
        }

        private void OnStop(object sender, RoutedEventArgs e)
        {
            Raise(StopReconnect);
        }

        private void OnRetry(object sender, RoutedEventArgs e)
        {
            Raise(Retry);
        }

        private void OnEdit(object sender, RoutedEventArgs e)
        {
            Raise(EditHost);
        }

        private void OnClose(object sender, RoutedEventArgs e)
        {
            Raise(CloseSession);
        }

        private void Raise(EventHandler handler)
        {
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }
    }
}
