using System;
using SshTool.Core.Common;

namespace SshTool.App.Infrastructure
{
    // R01 (C-02)：页面 async 入口的兜底日志。ServiceRegistry 未就绪或日志自身
    // 异常时静默降级——页面导航路径不允许因日志再抛一次。
    public static class AppLog
    {
        // R03 (C-05)：Forget 调用点的 logger 安全解析；服务未就绪返回 null
        //（Forget 容许 null logger，fire-and-forget 路径上绝不因取日志再抛）。
        public static ILogger Logger
        {
            get
            {
                ILogger log;
                return ServiceRegistry.TryGet(out log) ? log : null;
            }
        }

        // 真机诊断用（X04 §12.2）：Debug 级默认被 FileLogger.MinLevel 挡掉，
        // 设置→关于→日志级别=调试 才落盘。调用方只准传事件名与计数，不准传内容。
        public static void Debug(string tag, string message)
        {
            ILogger log;
            if (!ServiceRegistry.TryGet(out log))
            {
                return;
            }
            try
            {
                log.Log(LogLevel.Debug, tag, message);
            }
            catch (Exception)
            {
            }
        }

        // 临时真机诊断（2026-09-29，定位软键盘三条通路；定位完成后连同调用点一并删除）。
        // 走 Info 级而不是 Debug：真机 logLevel 默认 info，放 Debug 等于要求用户先去改设置——
        // 上一轮就是这么白跑一趟的。调用方只准传事件名与计数，不准传内容（§12.2 脱敏）。
        public static void Diag(string tag, string message)
        {
            ILogger log;
            if (!ServiceRegistry.TryGet(out log))
            {
                return;
            }
            try
            {
                log.Log(LogLevel.Info, tag, message);
            }
            catch (Exception)
            {
            }
        }

        public static void Error(string tag, string message, Exception ex)
        {
            ILogger log;
            if (!ServiceRegistry.TryGet(out log))
            {
                return;
            }
            try
            {
                // 脱敏惯例（同 AppServices 启动日志）：相位 + 异常类型名。
                // ex.Message 可能含主机名/用户名/路径，LogRedactor 不剥离这些，不进日志。
                string detail = ex == null
                    ? string.Empty
                    : "：" + ex.GetType().Name;
                log.Log(LogLevel.Error, tag, message + detail);
            }
            catch (Exception)
            {
            }
        }
    }
}
