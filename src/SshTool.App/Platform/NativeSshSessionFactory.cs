using SshTool.Core.Sessions;

namespace SshTool.App.Platform
{
    // N09b：ISshSessionFactory 的原生实现（App 启动时注入 Core 侧消费者）。
    public sealed class NativeSshSessionFactory : ISshSessionFactory
    {
        public ISshSession Create()
        {
            return new NativeSshSession();
        }
    }
}
