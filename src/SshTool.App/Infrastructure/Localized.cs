using System;
using System.Globalization;
using Windows.ApplicationModel.Resources;

namespace SshTool.App.Infrastructure
{
    // O13：代码里取本地化文案的唯一入口。
    //
    // 页面/对话框各自 new ResourceLoader 的写法散了一片，且 GetForCurrentView()
    // 只能在 UI 线程用——LifecycleService 的后台保活描述就不在 UI 线程。这里统一
    // 用 GetForViewIndependentUse()（任意线程可用），取不到时回退 GetForCurrentView()。
    //
    // fallback 参数是「资源缺失时的兜底文案」，不是文案的事实来源：真正的文案在
    // Strings\<lang>\Resources.resw 里，中英两份由 ResourceParityTests 保证 key 一致。
    // 留兜底是为了让资源表出问题时界面仍可读，而不是把中文写死在代码里。
    public static class Localized
    {
        private static ResourceLoader _loader;
        private static bool _loaderTried;
        private static readonly object Gate = new object();

        public static string Get(string key, string fallback)
        {
            if (string.IsNullOrEmpty(key))
            {
                return fallback ?? string.Empty;
            }
            try
            {
                ResourceLoader loader = Loader();
                if (loader != null)
                {
                    string value = loader.GetString(key);
                    if (!string.IsNullOrEmpty(value))
                    {
                        return value;
                    }
                }
            }
            catch (Exception)
            {
                // 资源表缺键/加载器不可用：用兜底，不让界面空白。
            }
            return fallback ?? string.Empty;
        }

        // 带占位符的文案（"{0}"）。区域格式跟随当前区域。
        public static string Format(string key, string fallback, params object[] args)
        {
            string template = Get(key, fallback);
            if (args == null || args.Length == 0)
            {
                return template;
            }
            try
            {
                return string.Format(CultureInfo.CurrentCulture, template, args);
            }
            catch (FormatException)
            {
                // 译文里的占位符写坏了：退回未格式化的模板，总好过抛异常。
                return template;
            }
        }

        private static ResourceLoader Loader()
        {
            lock (Gate)
            {
                if (_loaderTried)
                {
                    return _loader;
                }
                _loaderTried = true;
                try
                {
                    _loader = ResourceLoader.GetForViewIndependentUse();
                }
                catch (Exception)
                {
                    try
                    {
                        _loader = ResourceLoader.GetForCurrentView();
                    }
                    catch (Exception)
                    {
                        _loader = null;
                    }
                }
                return _loader;
            }
        }
    }
}
