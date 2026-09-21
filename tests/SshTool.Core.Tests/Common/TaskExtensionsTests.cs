using System;
using System.Threading.Tasks;
using SshTool.Core.Common;
using Xunit;

namespace SshTool.Core.Tests.Common
{
    public class TaskExtensionsTests
    {
        private sealed class RecordingLogger : ILogger
        {
            private readonly TaskCompletionSource<bool> _logged =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public LogLevel? Level { get; private set; }
            public string Tag { get; private set; }
            public string Message { get; private set; }

            public Task Logged
            {
                get { return _logged.Task; }
            }

            public void Log(LogLevel level, string tag, string message)
            {
                Level = level;
                Tag = tag;
                Message = message;
                _logged.TrySetResult(true);
            }
        }

        private sealed class ThrowingLogger : ILogger
        {
            public void Log(LogLevel level, string tag, string message)
            {
                throw new InvalidOperationException("logger broken");
            }
        }

        [Fact]
        public async Task Forget_CompletedTask_PassesThrough_NoLog()
        {
            var logger = new RecordingLogger();
            Task.CompletedTask.Forget("Test.Ok", logger);
            Task winner = await Task.WhenAny(logger.Logged, Task.Delay(200));
            // 正常任务直通：不记日志、不抛异常。
            Assert.NotSame(logger.Logged, winner);
            Assert.Null(logger.Tag);
        }

        [Fact]
        public async Task Forget_FaultedTask_LogsContextAndExceptionType()
        {
            var logger = new RecordingLogger();
            FailAsync().Forget("Test.Boom", logger);
            await Task.WhenAny(logger.Logged, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.True(logger.Logged.IsCompleted, "5 秒内未观察到日志：异常未被捕获记录");
            Assert.Equal(LogLevel.Error, logger.Level);
            Assert.Equal("Test.Boom", logger.Tag);
            Assert.Contains("InvalidOperationException", logger.Message);
            // ex.Message 可能夹带敏感信息，不进日志。
            Assert.DoesNotContain("sensitive-payload", logger.Message);
        }

        [Fact]
        public async Task Forget_FaultedTask_NullLogger_DoesNotThrow()
        {
            // async void 内若抛出会终结测试进程，跑到这里即证明未抛。
            FailAsync().Forget("Test.Boom", null);
            await Task.Delay(100);
        }

        [Fact]
        public async Task Forget_LoggerThrows_StillSwallowed()
        {
            FailAsync().Forget("Test.Boom", new ThrowingLogger());
            await Task.Delay(100);
        }

        private static async Task FailAsync()
        {
            await Task.Yield();
            throw new InvalidOperationException("sensitive-payload");
        }
    }
}
