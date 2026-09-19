using System;
using System.Threading.Tasks;
using SshTool.Core.Sessions;

namespace SshTool.App.Infrastructure
{
    public sealed class DispatcherUiDispatcher : IUiDispatcher
    {
        public void Post(Action action)
        {
            DispatcherHelper.Post(action);
        }

        public Task RunAsync(Func<Task> action)
        {
            return RunAsync(async () =>
            {
                await action().ConfigureAwait(true);
                return true;
            });
        }

        public async Task<T> RunAsync<T>(Func<Task<T>> action)
        {
            if (DispatcherHelper.HasThreadAccess)
            {
                return await action().ConfigureAwait(true);
            }
            var tcs = new TaskCompletionSource<T>();
            DispatcherHelper.Post(async () =>
            {
                try
                {
                    T result = await action().ConfigureAwait(true);
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
            return await tcs.Task.ConfigureAwait(true);
        }
    }
}
