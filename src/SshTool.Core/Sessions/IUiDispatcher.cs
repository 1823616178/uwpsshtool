using System;
using System.Threading.Tasks;

namespace SshTool.Core.Sessions
{
    // 01-DESIGN.md §4.2 / D06：属性变更与对话框经此后台线程封送到 UI。
    // 测试注入立即执行实现。
    public interface IUiDispatcher
    {
        void Post(Action action);
        Task RunAsync(Func<Task> action);
        Task<T> RunAsync<T>(Func<Task<T>> action);
    }

    public sealed class ImmediateDispatcher : IUiDispatcher
    {
        public void Post(Action action)
        {
            if (action != null)
            {
                action();
            }
        }

        public Task RunAsync(Func<Task> action)
        {
            return action == null ? Task.FromResult(0) : action();
        }

        public Task<T> RunAsync<T>(Func<Task<T>> action)
        {
            return action();
        }
    }
}
