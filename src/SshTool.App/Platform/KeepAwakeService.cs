using System;
using SshTool.App.Infrastructure;
using SshTool.Core.Common;
using SshTool.Core.Lifecycle;
using SshTool.Core.Sessions;
using SshTool.Core.Storage;
using Windows.System.Display;

namespace SshTool.App.Platform
{
    // P01：DisplayRequest 的生产网关。
    //
    // 单个 DisplayRequest 实例持有到底（SP06 实测模式）：RequestActive/RequestRelease
    // 必须落在同一个实例上；实例被 GC 前必须 Release，否则常亮标记泄漏。
    // Windows.System.Display.DisplayRequest 自 WP8.1 起可用，无需 ApiInformation 守卫
    //（min 15063 纪律：本文件所有 API 均 ≤15063）。
    // 异常只记日志不外抛：常亮是体验优化，绝不能为此崩溃或阻断调用方。
    internal sealed class DisplayRequestGateway : IDisplayKeepAwakeGateway
    {
        private readonly ILogger _logger;
        private DisplayRequest _request;

        public DisplayRequestGateway(ILogger logger)
        {
            _logger = logger;
        }

        public void RequestActive()
        {
            try
            {
                if (_request == null)
                {
                    _request = new DisplayRequest();
                }
                _request.RequestActive();
            }
            catch (Exception ex)
            {
                _logger?.Log(LogLevel.Warning, "KeepAwake", "request failed " + ex.GetType().Name);
                throw;
            }
        }

        public void RequestRelease()
        {
            try
            {
                if (_request != null)
                {
                    _request.RequestRelease();
                }
            }
            catch (Exception ex)
            {
                _logger?.Log(LogLevel.Warning, "KeepAwake", "release failed " + ex.GetType().Name);
            }
        }
    }

    // P01：屏幕常亮服务（App 层外壳）。
    //
    // 分工：判定全在 Core.KeepAwakePolicy（纯函数、可单测），本类只做三件事——
    // 从设置/会话/页面读状态、重算一次、经 DisplayRequestTracker 下发（计数保护，
    // 防四路事件重复申请/超额释放）。日志只记档位与计数，不记任何会话内容。
    //
    // 状态来源三处，任一变化都重算一次（对齐鸿蒙端 ScreenAwake.ets）：
    //   - 终端页显隐：TerminalPage.OnNavigatedTo/From → SetTerminalVisible；
    //   - 会话增删：LifecycleService 转发 SessionsChanged → NotifySessionsChanged；
    //   - 档位变更：设置 Changed 事件 → RefreshSettings。
    // 必须在 UI 线程调用（DisplayRequest 有线程亲和）；非 UI 线程一律封送。
    public sealed class KeepAwakeService
    {
        private readonly DisplayRequestTracker _tracker;
        private readonly SettingsRepository _settings;
        private readonly SessionManager _sessions;
        private readonly ILogger _logger;
        private bool _terminalVisible;
        private bool _applied;

        public KeepAwakeService(
            SettingsRepository settings,
            SessionManager sessions,
            ILogger logger,
            IDisplayKeepAwakeGateway gateway = null)
        {
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }
            if (sessions == null)
            {
                throw new ArgumentNullException("sessions");
            }
            _settings = settings;
            _sessions = sessions;
            _logger = logger;
            _tracker = new DisplayRequestTracker(gateway ?? new DisplayRequestGateway(logger));
        }

        public bool IsActive
        {
            get { return _tracker.ActiveCount > 0; }
        }

        // 终端页进入/离开（TerminalPage.OnNavigatedTo/From）。
        public void SetTerminalVisible(bool visible)
        {
            if (!DispatcherHelper.HasThreadAccess)
            {
                DispatcherHelper.Post(() => SetTerminalVisible(visible));
                return;
            }
            if (_terminalVisible == visible)
            {
                return;
            }
            _terminalVisible = visible;
            Refresh();
        }

        public void NotifySessionsChanged()
        {
            if (!DispatcherHelper.HasThreadAccess)
            {
                DispatcherHelper.Post(NotifySessionsChanged);
                return;
            }
            Refresh();
        }

        // 设置页改了 keepScreenOn 档位后调用（LifecycleService 转发 Changed 事件）。
        public void RefreshSettings()
        {
            if (!DispatcherHelper.HasThreadAccess)
            {
                DispatcherHelper.Post(RefreshSettings);
                return;
            }
            Refresh();
        }

        // 切后台：常亮标记在后台无意义（屏幕迟早锁定），无条件还电省电。
        public void OnBackground()
        {
            if (!DispatcherHelper.HasThreadAccess)
            {
                DispatcherHelper.Post(OnBackground);
                return;
            }
            _tracker.ReleaseAll();
            _applied = false;
        }

        // 回前台：按当前状态重算（用户可能在后台关掉了最后一个会话）。
        public void OnForeground()
        {
            if (!DispatcherHelper.HasThreadAccess)
            {
                DispatcherHelper.Post(OnForeground);
                return;
            }
            Refresh();
        }

        private void Refresh()
        {
            string mode;
            int active;
            try
            {
                mode = _settings.KeepScreenOn;
            }
            catch (Exception)
            {
                mode = KeepAwakePolicy.ModeSession;
            }
            try
            {
                active = _sessions.ActiveSessionCount;
            }
            catch (Exception)
            {
                active = 0;
            }
            bool want = KeepAwakePolicy.ShouldKeepScreenOn(mode, _terminalVisible, active);
            try
            {
                _tracker.Refresh(want);
            }
            catch (Exception)
            {
                // 网关已记日志；此处不再抛，调用方（页面导航）不能被常亮拖垮。
                return;
            }
            if (want != _applied)
            {
                _applied = want;
                _logger?.Log(LogLevel.Info, "KeepAwake",
                    "active=" + want + " mode=" + KeepAwakePolicy.NormalizeMode(mode)
                    + " sessions=" + active);
            }
        }
    }
}
