using System;

namespace SshTool.Core.Lifecycle
{
    // P01：终端页屏幕常亮判定（纯函数，可单测）。
    //
    // 对齐鸿蒙端 service/ScreenAwake.ets 的 shouldKeepScreenOn（同一真值表），
    // 按 01-DESIGN.md §10 裁剪为 W10M 形态：UWP 的 DisplayRequest 只在前台有效，
    // 切后台由 LifecycleService 统一释放，此处不设 foreground 参数——任务约定的
    // 签名 ShouldKeepScreenOn(mode, terminalVisible, activeSessions) 即全部输入。
    //
    // mode 取值与设置键 keepScreenOn 一致（never/session/always，见 SettingDefinitions）；
    // 未知值（含 null/空串）按 fail-safe 关断处理：不压着屏幕，宁可不断连（重连链兜底），
    // 也不让异常配置把电池耗干。
    public static class KeepAwakePolicy
    {
        public const string ModeNever = "never";
        public const string ModeSession = "session";
        public const string ModeAlways = "always";

        public static bool ShouldKeepScreenOn(string mode, bool terminalVisible, int activeSessions)
        {
            if (!terminalVisible)
            {
                return false;
            }
            if (string.Equals(mode, ModeAlways, StringComparison.Ordinal))
            {
                return true;
            }
            if (string.Equals(mode, ModeSession, StringComparison.Ordinal))
            {
                return activeSessions > 0;
            }
            return false;
        }

        // 设置仓库读到非法值时的回退（读路径本已回退默认，此为纵深防御）。
        public static string NormalizeMode(string mode)
        {
            if (string.Equals(mode, ModeNever, StringComparison.Ordinal)
                || string.Equals(mode, ModeSession, StringComparison.Ordinal)
                || string.Equals(mode, ModeAlways, StringComparison.Ordinal))
            {
                return mode;
            }
            return ModeSession;
        }
    }

    // DisplayRequest 的系统网关（窄接口）：生产实现走 Windows.System.Display，
    // 单测注入 fake。Core 不引用任何 UWP API（netstandard1.4 纪律）。
    public interface IDisplayKeepAwakeGateway
    {
        void RequestActive();
        void RequestRelease();
    }

    // P01：DisplayRequest 成对调用计数保护。
    //
    // 背景：RequestActive/RequestRelease 必须严格成对，多调 Release 会抛，
    // 而常亮开关会在「终端页显隐 × 会话增删 × 设置变更 × 前后台」四路事件里反复重算，
    // 不加计数器必然出现重复申请或超额释放（SP06 踩坑：PlatformSpikePage 靠 bool 位
    // 保护，仍挡不住多路并发重算）。
    //
    // 语义：引用计数。0→1 时真正 RequestActive；1→0 时真正 RequestRelease；
    // ReleaseAll 用于切后台/挂起时的无条件还电。网关抛错时计数不变
    //（没拿到的东西不能记账，否则后续 Release 会错位）。
    // 线程安全：调用方（KeepAwakeService）保证 UI 线程调用，内部再加锁兜底。
    public sealed class DisplayRequestTracker
    {
        private readonly IDisplayKeepAwakeGateway _gateway;
        private readonly object _sync = new object();
        private int _count;

        public DisplayRequestTracker(IDisplayKeepAwakeGateway gateway)
        {
            if (gateway == null)
            {
                throw new ArgumentNullException("gateway");
            }
            _gateway = gateway;
        }

        public int ActiveCount
        {
            get
            {
                lock (_sync)
                {
                    return _count;
                }
            }
        }

        public void Acquire()
        {
            lock (_sync)
            {
                if (_count == 0)
                {
                    _gateway.RequestActive();
                }
                _count++;
            }
        }

        public void Release()
        {
            lock (_sync)
            {
                if (_count == 0)
                {
                    return;
                }
                _count--;
                if (_count == 0)
                {
                    _gateway.RequestRelease();
                }
            }
        }

        public void ReleaseAll()
        {
            lock (_sync)
            {
                if (_count == 0)
                {
                    return;
                }
                _count = 0;
                _gateway.RequestRelease();
            }
        }

        // 按期望状态对账：want=true 且未持有 → Acquire；want=false → ReleaseAll。
        // 同一期望重复调用不产生多余系统调用（幂等）。
        public void Refresh(bool wantActive)
        {
            if (wantActive)
            {
                lock (_sync)
                {
                    if (_count == 0)
                    {
                        _gateway.RequestActive();
                        _count = 1;
                    }
                }
                return;
            }
            ReleaseAll();
        }
    }
}
