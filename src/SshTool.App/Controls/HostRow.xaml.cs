using System;
using SshTool.Core.Hosts;
using Windows.UI.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;

namespace SshTool.App.Controls
{
    public sealed partial class HostRow : UserControl
    {
        private bool _ignoreNextTap;

        public HostRow()
        {
            this.InitializeComponent();
            this.DataContextChanged += OnDataContextChanged;
        }

        public HostListRow Row { get; private set; }

        public event EventHandler ConnectRequested;
        public event EventHandler NewSessionRequested;
        public event EventHandler EditRequested;
        public event EventHandler DuplicateRequested;
        public event EventHandler SftpRequested;
        public event EventHandler DeleteRequested;

        private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            Row = args.NewValue as HostListRow;
            BindRow();
        }

        private void BindRow()
        {
            HostListRow row = Row;
            if (row == null)
            {
                return;
            }
            NameText.Text = row.Name ?? string.Empty;
            AddressText.Text = row.AddressLine ?? string.Empty;
            Dot.State = ToDot(row.Status);
            KeyBadge.Visibility = row.ShowKey ? Visibility.Visible : Visibility.Collapsed;
            TmuxBadge.Visibility = row.ShowTmux ? Visibility.Visible : Visibility.Collapsed;
            JumpBadge.Visibility = row.ShowJump ? Visibility.Visible : Visibility.Collapsed;
            TunnelBadge.Visibility = row.ShowTunnel ? Visibility.Visible : Visibility.Collapsed;
        }

        private static StatusDotState ToDot(HostListStatus status)
        {
            switch (status)
            {
                case HostListStatus.Connected:
                    return StatusDotState.Connected;
                case HostListStatus.Reconnecting:
                    return StatusDotState.Reconnecting;
                case HostListStatus.Connecting:
                    return StatusDotState.Connecting;
                case HostListStatus.Error:
                    return StatusDotState.Error;
                default:
                    return StatusDotState.Disconnected;
            }
        }

        private void OnTapped(object sender, TappedRoutedEventArgs e)
        {
            if (_ignoreNextTap)
            {
                _ignoreNextTap = false;
                e.Handled = true;
                return;
            }
            Raise(ConnectRequested);
        }

        private void OnHolding(object sender, HoldingRoutedEventArgs e)
        {
            if (e.HoldingState != HoldingState.Started)
            {
                return;
            }
            ShowMenu();
            _ignoreNextTap = true;
            e.Handled = true;
        }

        private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            ShowMenu();
            _ignoreNextTap = true;
            e.Handled = true;
        }

        private void OnMoreTapped(object sender, TappedRoutedEventArgs e)
        {
            ShowMenu();
            _ignoreNextTap = true;
            e.Handled = true;
        }

        private void ShowMenu()
        {
            FlyoutBase.SetAttachedFlyout(this, RowMenu);
            FlyoutBase.ShowAttachedFlyout(this);
        }

        private void OnConnectClick(object sender, RoutedEventArgs e)
        {
            Raise(ConnectRequested);
        }

        private void OnNewSessionClick(object sender, RoutedEventArgs e)
        {
            Raise(NewSessionRequested);
        }

        private void OnEditClick(object sender, RoutedEventArgs e)
        {
            Raise(EditRequested);
        }

        private void OnDuplicateClick(object sender, RoutedEventArgs e)
        {
            Raise(DuplicateRequested);
        }

        private void OnSftpClick(object sender, RoutedEventArgs e)
        {
            Raise(SftpRequested);
        }

        private void OnDeleteClick(object sender, RoutedEventArgs e)
        {
            Raise(DeleteRequested);
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
