using System;
using Windows.Foundation.Metadata;

namespace SshTool.App.Platform
{
    // P02：省电模式守望（终端页 Banner 的数据源，01-DESIGN.md §10）。
    //
    // W10M 约束：Windows.System.Power.PowerManager 是否存在必须用 ApiInformation
    // 守卫（min 15063 纪律）。直接引用缺失类型的方法会在 JIT 时抛，
    // 故所有触碰 PowerManager 的代码收拢在私有嵌套类 EnergySaverInterop 内——
    // 外层只用字符串做守卫判断，守卫通过后才进入内层方法，缺席系统上内层方法
    // 永远不会被 JIT。订阅/退订成对（终端页 OnNavigatedTo/From）。
    //
    // 事件在系统线程触发，调用方（TerminalPage）负责封送到 UI 线程。
    public sealed class EnergySaverWatcher
    {
        private bool _started;

        // 省电模式 API 是否可用（守卫未通过则 Banner 永不显示，fail-safe）。
        public bool IsAvailable
        {
            get { return EnergySaverInterop.IsPresent(); }
        }

        // 当前是否处于省电模式（不可用时恒为 false）。
        public bool IsEnergySaverOn
        {
            get { return IsAvailable && EnergySaverInterop.IsOn(); }
        }

        public event EventHandler Changed;

        public void Start()
        {
            if (_started || !IsAvailable)
            {
                return;
            }
            _started = true;
            try
            {
                EnergySaverInterop.Subscribe(OnSystemChanged);
            }
            catch (Exception)
            {
                _started = false;
            }
        }

        public void Stop()
        {
            if (!_started)
            {
                return;
            }
            _started = false;
            try
            {
                if (EnergySaverInterop.IsPresent())
                {
                    EnergySaverInterop.Unsubscribe(OnSystemChanged);
                }
            }
            catch (Exception)
            {
            }
        }

        private void OnSystemChanged(object sender, object e)
        {
            EventHandler handler = Changed;
            if (handler != null)
            {
                try
                {
                    handler(this, EventArgs.Empty);
                }
                catch (Exception)
                {
                }
            }
        }

        // 仅当 IsPresent() 为 true 时才可进入本类（任一方法被 JIT 即要求类型存在）。
        private static class EnergySaverInterop
        {
            private const string TypeName = "Windows.System.Power.PowerManager";

            public static bool IsPresent()
            {
                try
                {
                    return ApiInformation.IsTypePresent(TypeName)
                        && ApiInformation.IsEventPresent(TypeName, "EnergySaverStatusChanged")
                        && ApiInformation.IsPropertyPresent(TypeName, "EnergySaverStatus");
                }
                catch (Exception)
                {
                    return false;
                }
            }

            public static bool IsOn()
            {
                try
                {
                    return Windows.System.Power.PowerManager.EnergySaverStatus
                        == Windows.System.Power.EnergySaverStatus.On;
                }
                catch (Exception)
                {
                    return false;
                }
            }

            public static void Subscribe(EventHandler<object> handler)
            {
                Windows.System.Power.PowerManager.EnergySaverStatusChanged += handler;
            }

            public static void Unsubscribe(EventHandler<object> handler)
            {
                Windows.System.Power.PowerManager.EnergySaverStatusChanged -= handler;
            }
        }
    }
}
