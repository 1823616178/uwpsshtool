using System;
using SshTool.Core.Common;

namespace SshTool.App.Infrastructure
{
    // R01 (C-02)：页面 async 入口的兜底日志。ServiceRegistry 未就绪或日志自身
    // 异常时静默降级——页面导航路径不允许因日志再抛一次。
    public static class AppLog
    {
        public static void Error(string tag, string message, Exception ex)
        {
            ILogger log;
            if (!ServiceRegistry.TryGet(out log))
            {
                return;
            }
            try
            {
                string detail = ex == null
                    ? string.Empty
                    : "：" + ex.GetType().Name + " " + ex.Message;
                log.Log(LogLevel.Error, tag, message + detail);
            }
            catch (Exception)
            {
            }
        }
    }
}
