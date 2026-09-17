using System;
using System.Windows.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Controls
{
    public sealed partial class SectionHeader : UserControl
    {
        public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
            nameof(Title), typeof(string), typeof(SectionHeader),
            new PropertyMetadata(string.Empty, OnChanged));
        public static readonly DependencyProperty ActionTextProperty = DependencyProperty.Register(
            nameof(ActionText), typeof(string), typeof(SectionHeader),
            new PropertyMetadata(null, OnChanged));
        public static readonly DependencyProperty ActionCommandProperty = DependencyProperty.Register(
            nameof(ActionCommand), typeof(ICommand), typeof(SectionHeader),
            new PropertyMetadata(null));

        public SectionHeader()
        {
            this.InitializeComponent();
            this.Loaded += (s, e) => UpdateVisual();
        }

        public string Title
        {
            get { return (string)GetValue(TitleProperty); }
            set { SetValue(TitleProperty, value); }
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

        private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((SectionHeader)d).UpdateVisual();
        }

        private void UpdateVisual()
        {
            TitleText.Text = Title ?? string.Empty;
            ActionButton.Visibility = string.IsNullOrEmpty(ActionText) ? Visibility.Collapsed : Visibility.Visible;
            ActionButton.Content = ActionText;
        }

        private void OnActionClick(object sender, RoutedEventArgs e)
        {
            if (ActionCommand != null && ActionCommand.CanExecute(null))
            {
                ActionCommand.Execute(null);
            }
        }
    }
}
