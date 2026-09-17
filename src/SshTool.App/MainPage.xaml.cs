using SshTool.Core;
using SshTool.Native;
using Windows.UI.Xaml.Controls;

namespace SshTool.App
{
    public sealed partial class MainPage : Page
    {
        public MainPage()
        {
            this.InitializeComponent();
            CoreVersionText.Text = "Core: " + CoreInfo.Version;
            NativeVersionText.Text = "Native: " + NativeInfo.Version();
        }
    }
}
