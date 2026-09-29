using System;
using System.Windows.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Controls
{
    // V01b（05 §5.3）：normal/offline/filtered 三变体。Kind 只在 Glyph 为空时提供默认图标
    // （offline=IconCloudOff、filtered=IconSearch）；显式 Glyph 始终优先，既有使用点不受影响。
    public enum EmptyStateKind
    {
        Normal,
        Offline,
        Filtered
    }

    public sealed partial class EmptyState : UserControl
    {
        public static readonly DependencyProperty KindProperty = Register(nameof(Kind), typeof(EmptyStateKind), EmptyStateKind.Normal, OnChanged);
        public static readonly DependencyProperty GlyphProperty = Register(nameof(Glyph), typeof(string), string.Empty, OnChanged);
        public static readonly DependencyProperty TitleProperty = Register(nameof(Title), typeof(string), string.Empty, OnChanged);
        public static readonly DependencyProperty DescriptionProperty = Register(nameof(Description), typeof(string), string.Empty, OnChanged);
        public static readonly DependencyProperty PrimaryTextProperty = Register(nameof(PrimaryText), typeof(string), null, OnChanged);
        public static readonly DependencyProperty PrimaryCommandProperty = Register(nameof(PrimaryCommand), typeof(ICommand), null, OnChanged);
        public static readonly DependencyProperty SecondaryTextProperty = Register(nameof(SecondaryText), typeof(string), null, OnChanged);
        public static readonly DependencyProperty SecondaryCommandProperty = Register(nameof(SecondaryCommand), typeof(ICommand), null, OnChanged);
        public static readonly DependencyProperty PrimaryButtonStyleProperty = Register(nameof(PrimaryButtonStyle), typeof(Style), null, OnChanged);

        public event RoutedEventHandler PrimaryClick;
        public event RoutedEventHandler SecondaryClick;

        public EmptyState()
        {
            this.InitializeComponent();
            this.Loaded += (s, e) => UpdateVisual();
        }

        public EmptyStateKind Kind
        {
            get { return (EmptyStateKind)GetValue(KindProperty); }
            set { SetValue(KindProperty, value); }
        }

        public string Glyph
        {
            get { return (string)GetValue(GlyphProperty); }
            set { SetValue(GlyphProperty, value); }
        }

        public string Title
        {
            get { return (string)GetValue(TitleProperty); }
            set { SetValue(TitleProperty, value); }
        }

        public string Description
        {
            get { return (string)GetValue(DescriptionProperty); }
            set { SetValue(DescriptionProperty, value); }
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

        public Style PrimaryButtonStyle
        {
            get { return (Style)GetValue(PrimaryButtonStyleProperty); }
            set { SetValue(PrimaryButtonStyleProperty, value); }
        }

        public string SecondaryText
        {
            get { return (string)GetValue(SecondaryTextProperty); }
            set { SetValue(SecondaryTextProperty, value); }
        }

        public ICommand SecondaryCommand
        {
            get { return (ICommand)GetValue(SecondaryCommandProperty); }
            set { SetValue(SecondaryCommandProperty, value); }
        }

        private static DependencyProperty Register(string name, Type type, object defaultValue, PropertyChangedCallback callback)
        {
            return DependencyProperty.Register(name, type, typeof(EmptyState), new PropertyMetadata(defaultValue, callback));
        }

        private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((EmptyState)d).UpdateVisual();
        }

        private void UpdateVisual()
        {
            string glyph = Glyph;
            if (string.IsNullOrEmpty(glyph))
            {
                if (Kind == EmptyStateKind.Offline)
                {
                    glyph = (string)Application.Current.Resources["IconCloudOff"];
                }
                else if (Kind == EmptyStateKind.Filtered)
                {
                    glyph = (string)Application.Current.Resources["IconSearch"];
                }
            }
            GlyphIcon.Glyph = glyph ?? string.Empty;
            GlyphIcon.Visibility = string.IsNullOrEmpty(glyph) ? Visibility.Collapsed : Visibility.Visible;
            TitleText.Text = Title ?? string.Empty;
            TitleText.Visibility = string.IsNullOrEmpty(Title) ? Visibility.Collapsed : Visibility.Visible;
            DescriptionText.Text = Description ?? string.Empty;
            DescriptionText.Visibility = string.IsNullOrEmpty(Description) ? Visibility.Collapsed : Visibility.Visible;

            if (PrimaryButtonStyle != null)
            {
                PrimaryButton.Style = PrimaryButtonStyle;
            }

            PrimaryButton.Visibility = string.IsNullOrEmpty(PrimaryText) ? Visibility.Collapsed : Visibility.Visible;
            PrimaryButton.Content = PrimaryText;
            SecondaryButton.Visibility = string.IsNullOrEmpty(SecondaryText) ? Visibility.Collapsed : Visibility.Visible;
            SecondaryButton.Content = SecondaryText;
        }

        private void OnPrimaryClick(object sender, RoutedEventArgs e)
        {
            var h = PrimaryClick;
            if (h != null)
            {
                h(this, e);
            }
            if (PrimaryCommand != null && PrimaryCommand.CanExecute(null))
            {
                PrimaryCommand.Execute(null);
            }
        }

        private void OnSecondaryClick(object sender, RoutedEventArgs e)
        {
            var h = SecondaryClick;
            if (h != null)
            {
                h(this, e);
            }
            if (SecondaryCommand != null && SecondaryCommand.CanExecute(null))
            {
                SecondaryCommand.Execute(null);
            }
        }
    }
}
