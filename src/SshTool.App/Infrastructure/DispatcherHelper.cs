using System;
using SshTool.App.Infrastructure;
using SshTool.Core.Common;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.UI.Core;

namespace SshTool.App.Infrastructure
{
    // 01-DESIGN.md §4.2：native I/O 线程与 .NET 线程池的回调统一经 CoreDispatcher.RunAsync 封送
    //（15063 无 DispatcherQueue）。
    // 注意：Window.Current 是**线程静态**的，后台线程上恒为 null，不能在这里用；
    // 启动时在 UI 线程缓存 CoreDispatcher，兜底用 CoreApplication.MainView（可跨线程访问）。
    public static class DispatcherHelper
    {
        private static CoreDispatcher _dispatcher;

        public static void Initialize(CoreDispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        public static CoreDispatcher Dispatcher
        {
            get
            {
                if (_dispatcher == null)
                {
                    var view = CoreApplication.MainView;
                    var window = view != null ? view.CoreWindow : null;
                    if (window != null)
                    {
                        _dispatcher = window.Dispatcher;
                    }
                }
                return _dispatcher;
            }
        }

        public static bool HasThreadAccess
        {
            get
            {
                var dispatcher = Dispatcher;
                return dispatcher != null && dispatcher.HasThreadAccess;
            }
        }

        public static async Task RunOnUiThreadAsync(Action action, CoreDispatcherPriority priority = CoreDispatcherPriority.Normal)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }
            var dispatcher = Dispatcher;
            if (dispatcher == null)
            {
                throw new InvalidOperationException("UI thread not ready: call DispatcherHelper.Initialize in OnLaunched first.");
            }
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

        // 即发即忘的封送（native 事件回调用；异常不会拖垮调用方线程）
        public static void Post(Action action, CoreDispatcherPriority priority = CoreDispatcherPriority.Normal)
        {
            var dispatcher = Dispatcher;
            if (dispatcher == null || action == null)
            {
                return;
            }
            if (dispatcher.HasThreadAccess)
            {
                action();
                return;
            }
            // O05：UI 封送原语自身的派发失败也要可观察——别处正是靠它做跨线程
            // 安全的。日志走文件、不经 dispatcher，因此不会递归回到这里。
            dispatcher.RunAsync(priority, () => action()).AsTask()
                .Forget("DispatcherHelper.Post", AppLog.Logger);
        }
    }
}
