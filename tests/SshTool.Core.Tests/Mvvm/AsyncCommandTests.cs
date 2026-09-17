using System;
using System.Threading;
using System.Threading.Tasks;
using SshTool.Core.Mvvm;
using Xunit;

namespace SshTool.Core.Tests.Mvvm
{
    public class AsyncCommandTests
    {
        [Fact]
        public async Task Execute_RunsToCompletion_AndTogglesIsRunning()
        {
            var cmd = new AsyncCommand(() => Task.Delay(20));
            Assert.True(cmd.CanExecute(null));
            cmd.Execute(null);
            Assert.True(cmd.IsRunning);
            Assert.False(cmd.CanExecute(null));
            await WaitFor(() => !cmd.IsRunning);
            Assert.True(cmd.CanExecute(null));
        }

        [Fact]
        public async Task Execute_WhileRunning_IsIgnored()
        {
            int calls = 0;
            var cmd = new AsyncCommand(async () =>
            {
                Interlocked.Increment(ref calls);
                await Task.Delay(50);
            });
            cmd.Execute(null);
            cmd.Execute(null);
            cmd.Execute(null);
            await WaitFor(() => !cmd.IsRunning);
            Assert.Equal(1, calls);
        }

        [Fact]
        public async Task Execute_Exception_GoesToHandler()
        {
            Exception caught = null;
            var cmd = new AsyncCommand(
                () => throw new InvalidOperationException("boom"),
                onError: ex => caught = ex);
            cmd.Execute(null);
            await WaitFor(() => !cmd.IsRunning);
            Assert.IsType<InvalidOperationException>(caught);
        }

        [Fact]
        public void CanExecute_RespectsUserPredicate()
        {
            var cmd = new AsyncCommand(() => Task.CompletedTask, canExecute: () => false);
            Assert.False(cmd.CanExecute(null));
        }

        private static async Task WaitFor(Func<bool> condition)
        {
            for (int i = 0; i < 100; i++)
            {
                if (condition())
                {
                    return;
                }
                await Task.Delay(10);
            }
            Assert.True(false, "等待条件超时");
        }
    }
}
