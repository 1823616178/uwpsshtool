using SshTool.Core.Sessions;

namespace SshTool.App.Platform
{
    // N09b：ISshSessionFactory 的原生实现（App 启动时注入 Core 侧消费者）。
    // K03：持有与 SessionManager 共用的 NativeSshAgent，多会话复用同一份
    // native 托管内存。
    public sealed class NativeSshSessionFactory : ISshSessionFactory
    {
        private readonly NativeSshAgent _agent;

        public NativeSshSessionFactory()
            : this(null)
        {
        }

        public NativeSshSessionFactory(NativeSshAgent agent)
        {
            _agent = agent;
        }

        public ISshSession Create()
        {
            return new NativeSshSession(_agent);
        }
    }
}
