using System;
using System.Threading.Tasks;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace SshTool.App.Infrastructure
{
    public static class DispatcherHelper
    {
        public static async Task RunOnUiThreadAsync(Action action, CoreDispatcherPriority priority = CoreDispatcherPriority.Normal)
        {
            var dispatcher = Window.Current.Dispatcher;
            if (dispatcher.HasThreadAccess)
            {
                action();
                return;
            }
            var tcs = new TaskCompletionSource<bool>();
            await dispatcher.RunAsync(priority, () =>
            {
                try
                {
                    action();
                    tcs.SetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            await tcs.Task;
        }
    }
}
