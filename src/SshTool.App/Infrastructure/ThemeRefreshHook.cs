using System;
using SshTool.App.Platform;
using Windows.UI.Xaml;

namespace SshTool.App.Infrastructure
{
    // ui/fix-pass：代码里赋值的主题画刷（ThemeService.ResolveBrush）不会像 {ThemeResource} 那样
    // 随主题切换自动重算；15063 也没有 FrameworkElement.ActualThemeChanged（16299 起）。
    // 控件在构造函数里 ThemeRefreshHook.Attach(this, Refresh) 即可：
    //   - Loaded 时订阅 ThemeService.ThemeChanged，Unloaded 时解除（不让静态事件拽住死控件）；
    //   - 重新 Loaded（列表容器回收、页面返回）时若错过了广播（Version 变了）立即补一次 refresh；
    //   - refresh 只在 UI 线程调用（ThemeChanged 在 UI 线程触发）。
    public sealed class ThemeRefreshHook
    {
        private readonly Action _refresh;
        private int _seenVersion;
        private bool _subscribed;

        private ThemeRefreshHook(FrameworkElement element, Action refresh)
        {
            _refresh = refresh;
            _seenVersion = ThemeService.Version;
            element.Loaded += OnLoaded;
            element.Unloaded += OnUnloaded;
        }

        public static ThemeRefreshHook Attach(FrameworkElement element, Action refresh)
        {
            if (element == null)
            {
                throw new ArgumentNullException(nameof(element));
            }
            if (refresh == null)
            {
                throw new ArgumentNullException(nameof(refresh));
            }
            return new ThemeRefreshHook(element, refresh);
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!_subscribed)
            {
                ThemeService.ThemeChanged += OnThemeChanged;
                _subscribed = true;
            }
            if (_seenVersion != ThemeService.Version)
            {
                Run();
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_subscribed)
            {
                ThemeService.ThemeChanged -= OnThemeChanged;
                _subscribed = false;
            }
        }

        private void OnThemeChanged(object sender, EventArgs e)
        {
            Run();
        }

        private void Run()
        {
            _seenVersion = ThemeService.Version;
            try
            {
                _refresh();
            }
            catch (Exception ex)
            {
                SshTool.Core.Common.ILogger log = AppLog.Logger;
                if (log != null)
                {
                    log.Log(SshTool.Core.Common.LogLevel.Warning, "Theme", "refresh failed " + ex.GetType().Name);
                }
            }
        }
    }
}
