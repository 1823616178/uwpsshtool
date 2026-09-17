using System;
using System.Windows.Input;
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

        public Banner()
        {
            this.InitializeComponent();
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

        // 主题字典解析（跟随当前 RequestedTheme）
        internal static Brush ResolveThemedBrush(string key)
        {
            var app = Application.Current;
            var element = Window.Current.Content as FrameworkElement;
            bool light = (element != null && element.RequestedTheme == ElementTheme.Light)
                || (element == null && app.RequestedTheme == ApplicationTheme.Light);
            return ResolveThemed(app.Resources, key, light ? "Light" : "Dark") as Brush;
        }

        internal static object ResolveThemed(ResourceDictionary resources, string key, string theme)
        {
            object value;
            if (resources.TryGetValue(key, out value))
            {
                return value;
            }
            foreach (var merged in resources.MergedDictionaries)
            {
                value = ResolveThemed(merged, key, theme);
                if (value != null)
                {
                    return value;
                }
            }
            ResourceDictionary themed;
            if (resources.ThemeDictionaries.TryGetValue(theme, out value))
            {
                themed = value as ResourceDictionary;
                if (themed != null)
                {
                    return ResolveThemed(themed, key, theme);
                }
            }
            return null;
        }

        private void OnActionClick(object sender, RoutedEventArgs e)
        {
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
