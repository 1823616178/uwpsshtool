using SshTool.App.Platform;
using System;
using System.Globalization;
using SshTool.App.Infrastructure;
using SshTool.Core.Common;
using SshTool.Core.Hosts;
using Windows.UI;
using Windows.UI.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace SshTool.App.Controls
{
    public sealed partial class HostRow : UserControl
    {
        private static readonly Brush WhiteBrush = new SolidColorBrush(Colors.White);
        private static readonly Brush BlackBrush = new SolidColorBrush(Colors.Black);
        private bool _ignoreNextTap;

        public HostRow()
        {
            this.InitializeComponent();
            // ui/fix-pass：代码赋值的主题画刷随 ThemeService.ThemeChanged 重算。
            ThemeRefreshHook.Attach(this, BindRow);
            this.DataContextChanged += OnDataContextChanged;
        }

        public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(
            nameof(IsCompact), typeof(bool), typeof(HostRow),
            new PropertyMetadata(false, OnIsCompactChanged));

        public bool IsCompact
        {
            get { return (bool)GetValue(IsCompactProperty); }
            set { SetValue(IsCompactProperty, value); }
        }

        private static void OnIsCompactChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var hostRow = (HostRow)d;
            if (hostRow.RowCtl != null)
            {
                hostRow.RowCtl.IsCompact = (bool)e.NewValue;
            }
        }

        public HostListRow Row { get; private set; }

        public event EventHandler ConnectRequested;
        public event EventHandler NewSessionRequested;
        public event EventHandler EditRequested;
        public event EventHandler DuplicateRequested;
        public event EventHandler SftpRequested;
        public event EventHandler DeleteRequested;
        // W03：收藏切换 / 固定到开始屏幕。
        public event EventHandler FavoriteRequested;
        public event EventHandler PinRequested;

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
            RowCtl.Title = row.Name ?? string.Empty;
            RowCtl.Subtitle = row.AddressLine ?? string.Empty;
            RowCtl.IsCompact = IsCompact;
            RowCtl.TitleBadgeGlyph = row.IsFavorite
                ? (string)Application.Current.Resources["IconFavoriteStarFill"]
                : null;
            Dot.State = ToDot(row.Status);
            AvatarText.Text = ExtractInitial(row.Name, row.AddressLine);
            ApplyAvatarColor(row.GroupColor);
            KeyBadge.Visibility = row.ShowKey ? Visibility.Visible : Visibility.Collapsed;
            TmuxBadge.Visibility = row.ShowTmux ? Visibility.Visible : Visibility.Collapsed;
            JumpBadge.Visibility = row.ShowJump ? Visibility.Visible : Visibility.Collapsed;
            TunnelBadge.Visibility = row.ShowTunnel ? Visibility.Visible : Visibility.Collapsed;
            FavoriteItem.Text = row.IsFavorite
                ? Localized.Get("HostRow_MenuUnfavorite", "取消收藏")
                : Localized.Get("HostRow_MenuFavoriteText", "收藏");
        }

        private static string ExtractInitial(string name, string addressLine)
        {
            string candidate = (name ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(candidate))
            {
                candidate = (addressLine ?? string.Empty).Trim();
            }
            if (string.IsNullOrEmpty(candidate))
            {
                return "?";
            }
            TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(candidate);
            if (enumerator.MoveNext())
            {
                string element = enumerator.GetTextElement();
                return element.ToUpperInvariant();
            }
            return "?";
        }

        private void ApplyAvatarColor(string groupColorHex)
        {
            byte r;
            byte g;
            byte b;
            if (!string.IsNullOrEmpty(groupColorHex) && ColorHex.TryParse(groupColorHex, out r, out g, out b))
            {
                AvatarBorder.Background = new SolidColorBrush(Color.FromArgb(255, r, g, b));
                AvatarText.Foreground = ColorHex.NeedsDarkText(r, g, b) ? BlackBrush : WhiteBrush;
            }
            else
            {
                AvatarBorder.Background = ThemeService.ResolveBrush("AppSurfaceAltBrush");
                AvatarText.Foreground = ThemeService.ResolveBrush("AppTextBrush");
            }
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

        // V02：行点按=连接（AppListRow.Click；尾槽 Chevron 的点击不冒泡到这里）。
        // 长按/右键弹过菜单后抑制紧随的这次点击（同原 OnTapped 的去抖）。
        private void OnRowClick(object sender, EventArgs e)
        {
            if (_ignoreNextTap)
            {
                _ignoreNextTap = false;
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
            try
            {
                RowMenu.ShowAt(this, e.GetPosition(this));
            }
            catch (Exception)
            {
                ShowMenu();
            }
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

        private void OnFavoriteClick(object sender, RoutedEventArgs e)
        {
            Raise(FavoriteRequested);
        }

        private void OnPinClick(object sender, RoutedEventArgs e)
        {
            Raise(PinRequested);
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
