using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Controls
{
    public sealed partial class TransientToast : UserControl
    {
        private static readonly TimeSpan DisplayDuration = TimeSpan.FromMilliseconds(1500);
        private readonly DispatcherTimer _timer;

        public TransientToast()
        {
            this.InitializeComponent();
            _timer = new DispatcherTimer { Interval = DisplayDuration };
            _timer.Tick += (s, e) => Hide();
        }

        public void Show(string message)
        {
            _timer.Stop();
            MessageText.Text = message ?? string.Empty;
            Toast.Visibility = Visibility.Visible;
            _timer.Start();
        }

        public void Hide()
        {
            _timer.Stop();
            Toast.Visibility = Visibility.Collapsed;
        }
    }
}
