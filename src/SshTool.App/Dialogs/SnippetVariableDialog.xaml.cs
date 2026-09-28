using System.Collections.Generic;
using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using SshTool.Core.Terminal;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Dialogs
{
    public sealed class SnippetVariableResult
    {
        public bool Confirmed { get; set; }

        public IDictionary<string, string> Values { get; set; }
    }

    // U13：片段待填变量收集框。预览区随输入实时重渲染
    //（内置变量已代入，未填的变量暂时显示为空）。
    public sealed partial class SnippetVariableDialog : ContentDialog
    {
        private string _template = string.Empty;
        private SnippetBuiltin _builtin;
        private readonly List<TextBox> _inputs = new List<TextBox>();
        private readonly List<string> _names = new List<string>();

        private SnippetVariableDialog()
        {
            this.InitializeComponent();
        }

        public static async Task<SnippetVariableResult> ShowAsync(
            string snippetName, string template,
            SnippetBuiltin builtin, IReadOnlyList<string> variables)
        {
            var dialog = new SnippetVariableDialog();
            dialog.Title = string.IsNullOrEmpty(snippetName) ? "发送片段" : snippetName;
            dialog.PrimaryButtonText = Localized.Get("SnippetVariable_Send", "发送");
            dialog.SecondaryButtonText = Localized.Get("Dialog_Cancel", "取消");
            dialog._template = template ?? string.Empty;
            dialog._builtin = builtin ?? new SnippetBuiltin();
            dialog.BuildPrompts(variables);
            dialog.RefreshPreview();
            ContentDialogResult result =
                await ServiceRegistry.Get<DialogService>().ShowAsync(dialog);
            IDictionary<string, string> values = null;
            if (result == ContentDialogResult.Primary)
            {
                values = dialog.CollectValues();
            }
            return new SnippetVariableResult
            {
                Confirmed = result == ContentDialogResult.Primary,
                Values = values
            };
        }

        private void BuildPrompts(IReadOnlyList<string> variables)
        {
            var labelStyle = (Style)Application.Current.Resources["BodyTextStyle"];
            var labelMargin = (Thickness)Application.Current.Resources["GapMdTop"];
            var inputMargin = (Thickness)Application.Current.Resources["GapXsTop"];
            if (variables == null)
            {
                return;
            }
            for (int i = 0; i < variables.Count; i++)
            {
                string name = variables[i];
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }
                var label = new TextBlock
                {
                    Text = "${" + name + "}",
                    Style = labelStyle,
                    Margin = labelMargin
                };
                var box = new TextBox
                {
                    Header = name,
                    Margin = inputMargin
                };
                box.TextChanged += OnInputChanged;
                PromptsPanel.Children.Add(label);
                PromptsPanel.Children.Add(box);
                _inputs.Add(box);
                _names.Add(name);
            }
        }

        private void OnInputChanged(object sender, TextChangedEventArgs e)
        {
            RefreshPreview();
        }

        private void RefreshPreview()
        {
            PreviewText.Text = SnippetTemplate.RenderWithBuiltin(
                _template, _builtin, CollectValues());
        }

        private IDictionary<string, string> CollectValues()
        {
            var values = new Dictionary<string, string>(System.StringComparer.Ordinal);
            for (int i = 0; i < _names.Count && i < _inputs.Count; i++)
            {
                values[_names[i]] = _inputs[i].Text ?? string.Empty;
            }
            return values;
        }
    }
}
