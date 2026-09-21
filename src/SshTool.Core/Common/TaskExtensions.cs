using System;
using System.Threading.Tasks;

namespace SshTool.Core.Common
{
    // R03 (C-05)：fire-and-forget 任务的唯一观察点。调用方同步拿回返回、任务在后台
    // 继续；异常在 continuation 内捕获并按「模块.动作 + 异常类型名」记录——ex.Message
    // 可能夹带主机名/用户名/路径，一律不进日志（脱敏由 logger 实现保证，但 Message
    // 不属于 LogRedactor 的剥离项）。async void 是为让调用点同步返回；方法内任何
    // 异常都已就地吞掉，永不 rethrow（async void 上抛出会终结进程）。
    //
    // 例外：Platform/FileLogger 的后台写盘自愈路径有意不用本方法（日志失败再记日志
    // 会递归），保留其私有的无参 Forget。
    public static class TaskExtensions
    {
        public static async void Forget(this Task task, string context, ILogger logger = null)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (logger == null)
                {
                    return;
                }
                string tag = context ?? "Forget";
                try
                {
                    logger.Log(LogLevel.Error, tag, tag + "：" + ex.GetType().Name);
                }
                catch (Exception)
                {
                    // 日志自身失败不再传播：Forget 绝不抛出。
                }
            }
        }
    }
}
