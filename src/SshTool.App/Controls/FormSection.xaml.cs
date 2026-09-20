using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Controls
{
    public enum FormSectionState
    {
        Normal,
        Error,
        ReadOnly
    }

    public sealed partial class FormSection : UserControl
    {
        public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
            nameof(Header), typeof(string), typeof(FormSection),
            new PropertyMetadata(string.Empty, OnChanged));
        public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
            nameof(Description), typeof(string), typeof(FormSection),
            new PropertyMetadata(string.Empty, OnChanged));
        // 命名避开 UserControl.Content（同 SurfaceCard.CardContent 的考虑），消费方写
        // <controls:FormSection.SectionContent>...</controls:FormSection.SectionContent>。
        public static readonly DependencyProperty SectionContentProperty = DependencyProperty.Register(
            nameof(SectionContent), typeof(object), typeof(FormSection),
            new PropertyMetadata(null, OnChanged));
        public static readonly DependencyProperty ErrorMessageProperty = DependencyProperty.Register(
            nameof(ErrorMessage), typeof(string), typeof(FormSection),
            new PropertyMetadata(string.Empty, OnChanged));
        public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
            nameof(State), typeof(FormSectionState), typeof(FormSection),
            new PropertyMetadata(FormSectionState.Normal, OnChanged));

        public FormSection()
        {
            this.InitializeComponent();
            this.Loaded += (s, e) => UpdateVisual();
        }

        public string Header
        {
            get { return (string)GetValue(HeaderProperty); }
            set { SetValue(HeaderProperty, value); }
        }

        public string Description
        {
            get { return (string)GetValue(DescriptionProperty); }
            set { SetValue(DescriptionProperty, value); }
        }

        public object SectionContent
        {
            get { return GetValue(SectionContentProperty); }
            set { SetValue(SectionContentProperty, value); }
        }

        // 仅 State=Error 时显示（验证信息与错误态显式绑定，避免半吊子红色提示）。
        public string ErrorMessage
        {
            get { return (string)GetValue(ErrorMessageProperty); }
            set { SetValue(ErrorMessageProperty, value); }
        }

        public FormSectionState State
        {
            get { return (FormSectionState)GetValue(StateProperty); }
            set { SetValue(StateProperty, value); }
        }

        private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((FormSection)d).UpdateVisual();
        }

        private void UpdateVisual()
        {
            var gapXsTop = (Thickness)Application.Current.Resources["GapXsTop"];
            var gapSmTop = (Thickness)Application.Current.Resources["GapSmTop"];
            var noPad = (Thickness)Application.Current.Resources["PadNone"];

            bool hasHeader = !string.IsNullOrEmpty(Header);
            HeaderText.Text = Header ?? string.Empty;
            HeaderText.Visibility = hasHeader ? Visibility.Visible : Visibility.Collapsed;

            bool hasDescription = !string.IsNullOrEmpty(Description);
            DescriptionText.Text = Description ?? string.Empty;
            DescriptionText.Visibility = hasDescription ? Visibility.Visible : Visibility.Collapsed;
            DescriptionText.Margin = hasDescription ? gapXsTop : noPad;

            ContentSlot.Content = SectionContent;
            ContentSlot.Margin = (hasHeader || hasDescription) ? gapSmTop : noPad;

            bool showError = State == FormSectionState.Error && !string.IsNullOrEmpty(ErrorMessage);
            ErrorText.Text = showError ? ErrorMessage : string.Empty;
            ErrorText.Visibility = showError ? Visibility.Visible : Visibility.Collapsed;
            ErrorText.Margin = showError ? gapXsTop : noPad;

            // 评审回补：readonly=触控只读（断 IsHitTestVisible），不阻断 Tab 键盘焦点——
            // 15063 无子树键盘导航开关，键盘/Continuum 场景由消费方自行禁用内容控件。
            bool readOnly = State == FormSectionState.ReadOnly;
            ContentSlot.Opacity = readOnly
                ? (double)Application.Current.Resources["DisabledOpacity"]
                : 1.0;
            ContentSlot.IsHitTestVisible = !readOnly;
        }
    }
}
