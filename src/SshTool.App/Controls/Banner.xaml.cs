using System;
using System.Windows.Input;
using SshTool.App.Infrastructure;
using SshTool.App.Platform;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace SshTool.App.Controls
{
    public enum BannerSeverity
    {
        Info,
        Warning,
        Error,
        Success
    }

    public sealed partial class Banner : UserControl
    {
        public static readonly DependencyProperty SeverityProperty = Register(nameof(Severity), typeof(BannerSeverity), BannerSeverity.Info, OnVisualChanged);
        public static readonly DependencyProperty TitleProperty = Register(nameof(Title), typeof(string), string.Empty, OnVisualChanged);
        public static readonly DependencyProperty MessageProperty = Register(nameof(Message), typeof(string), string.Empty, OnVisualChanged);
        public static readonly DependencyProperty ActionTextProperty = Register(nameof(ActionText), typeof(string), null, OnVisualChanged);
        public static readonly DependencyProperty ActionCommandProperty = Register(nameof(ActionCommand), typeof(ICommand), null);

        public event EventHandler Closed;

        // V01b（05 §5.3 InlineBanner 角色）：页面级 Banner 的重试入口——消费方可在
        // XAML/代码里直接挂事件，不必为「重试」单独包 ICommand。事件先于 ActionCommand 触发。
        public event EventHandler ActionClick;

        public Banner()
        {
            this.InitializeComponent();
            // ui/fix-pass：代码赋值的主题画刷随 ThemeService.ThemeChanged 重算。
            ThemeRefreshHook.Attach(this, UpdateVisual);
            this.Loaded += (s, e) => UpdateVisual();
        }

        public BannerSeverity Severity
        {
            get { return (BannerSeverity)GetValue(SeverityProperty); }
            set { SetValue(SeverityProperty, value); }
        }

        public string Title
        {
            get { return (string)GetValue(TitleProperty); }
            set { SetValue(TitleProperty, value); }
        }

        public string Message
        {
            get { return (string)GetValue(MessageProperty); }
            set { SetValue(MessageProperty, value); }
        }

        public string ActionText
        {
            get { return (string)GetValue(ActionTextProperty); }
            set { SetValue(ActionTextProperty, value); }
        }

        public ICommand ActionCommand
        {
            get { return (ICommand)GetValue(ActionCommandProperty); }
            set { SetValue(ActionCommandProperty, value); }
        }

        private static DependencyProperty Register(string name, Type type, object defaultValue, PropertyChangedCallback callback = null)
        {
            return DependencyProperty.Register(name, type, typeof(Banner), new PropertyMetadata(defaultValue, callback));
        }

        private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((Banner)d).UpdateVisual();
        }

        private void UpdateVisual()
        {
            string glyph;
            string brushKey;
            switch (Severity)
            {
                case BannerSeverity.Warning:
                    glyph = (string)Application.Current.Resources["IconWarning"];
                    brushKey = "AppWarningBrush";
                    break;
                case BannerSeverity.Error:
                    glyph = (string)Application.Current.Resources["IconError"];
                    brushKey = "AppDangerBrush";
                    break;
                case BannerSeverity.Success:
                    glyph = (string)Application.Current.Resources["IconConnect"];
                    brushKey = "AppSuccessBrush";
                    break;
                default:
                    glyph = (string)Application.Current.Resources["IconInfo"];
                    brushKey = "AppInfoBrush";
                    break;
            }
            SeverityIcon.Glyph = glyph;
            var brush = ResolveThemedBrush(brushKey);
            SeverityIcon.Foreground = brush;

            TitleText.Text = Title ?? string.Empty;
            TitleText.Visibility = string.IsNullOrEmpty(Title) ? Visibility.Collapsed : Visibility.Visible;
            MessageText.Text = Message ?? string.Empty;
            MessageText.Visibility = string.IsNullOrEmpty(Message) ? Visibility.Collapsed : Visibility.Visible;

            bool hasAction = !string.IsNullOrEmpty(ActionText);
            ActionButton.Visibility = hasAction ? Visibility.Visible : Visibility.Collapsed;
            if (hasAction)
            {
                ActionButton.Content = ActionText;
            }
        }

        // 主题字典解析：委托 ThemeService（有效主题由 Core ThemeKeyResolver 统一判定）。
        // ui/fix-pass：旧实现按 root.RequestedTheme == Light 判断，「跟随系统」时根元素是
        // Default，系统浅色也被当成 Dark（单测 ThemeKeyResolverTests 覆盖该回归）。
        internal static Brush ResolveThemedBrush(string key)
        {
            return ThemeService.ResolveBrush(key);
        }

        private void OnActionClick(object sender, RoutedEventArgs e)
        {
            EventHandler handler = ActionClick;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
            if (ActionCommand != null && ActionCommand.CanExecute(null))
            {
                ActionCommand.Execute(null);
            }
        }

        private void OnCloseTapped(object sender, TappedRoutedEventArgs e)
        {
            Root.Visibility = Visibility.Collapsed;
            Closed?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }
}
