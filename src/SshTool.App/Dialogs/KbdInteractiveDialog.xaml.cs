using System.Collections.Generic;
using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Dialogs
{
    public sealed class KbdInteractivePrompt
    {
        public string Prompt { get; set; }
        public bool Echo { get; set; }
    }

    public sealed class KbdInteractiveDialogResult
    {
        public string[] Answers { get; set; }
        public bool Cancelled { get; set; }
    }

    public sealed partial class KbdInteractiveDialog : ContentDialog
    {
        private KbdInteractiveDialog()
        {
            this.InitializeComponent();
        }

        // 输入控件由 prompt 列表动态生成；应答读出后清空控件（PasswordBox 可能含敏感内容）。
        public static async Task<KbdInteractiveDialogResult> ShowAsync(
            string name, string instruction, IReadOnlyList<KbdInteractivePrompt> prompts)
        {
            var dialog = new KbdInteractiveDialog();
            dialog.NameText.Text = name ?? string.Empty;
            dialog.NameText.Visibility = string.IsNullOrEmpty(name) ? Visibility.Collapsed : Visibility.Visible;
            dialog.InstructionText.Text = instruction ?? string.Empty;
            dialog.InstructionText.Visibility = string.IsNullOrEmpty(instruction) ? Visibility.Collapsed : Visibility.Visible;

            var bodyStyle = (Style)Application.Current.Resources["BodyTextStyle"];
            var labelMargin = (Thickness)Application.Current.Resources["GapMdTop"];
            var inputMargin = (Thickness)Application.Current.Resources["GapXsTop"];
            var inputs = new List<Control>();
            for (int i = 0; i < prompts.Count; i++)
            {
                var label = new TextBlock
                {
                    Text = prompts[i].Prompt,
                    Style = bodyStyle,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = labelMargin
                };
                Control input = prompts[i].Echo ? (Control)new TextBox() : new PasswordBox();
                input.Margin = inputMargin;
                dialog.PromptsPanel.Children.Add(label);
                dialog.PromptsPanel.Children.Add(input);
                inputs.Add(input);
            }

            var result = await ServiceRegistry.Get<DialogService>().ShowAsync(dialog);
            string[] answers = null;
            if (result == ContentDialogResult.Primary)
            {
                answers = new string[inputs.Count];
                for (int i = 0; i < inputs.Count; i++)
                {
                    var box = inputs[i] as PasswordBox;
                    if (box != null)
                    {
                        answers[i] = box.Password;
                        box.Password = string.Empty;
                    }
                    else
                    {
                        var text = (TextBox)inputs[i];
                        answers[i] = text.Text;
                        text.Text = string.Empty;
                    }
                }
            }
            else
            {
                for (int i = 0; i < inputs.Count; i++)
                {
                    var box = inputs[i] as PasswordBox;
                    if (box != null)
                    {
                        box.Password = string.Empty;
                    }
                }
            }
            return new KbdInteractiveDialogResult
            {
                Answers = answers,
                Cancelled = result != ContentDialogResult.Primary
            };
        }
    }
}
