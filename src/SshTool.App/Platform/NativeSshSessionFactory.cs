using SshTool.Core.Sessions;
using SshTool.Core.Storage;

namespace SshTool.App.Platform
{
    // N09b：ISshSessionFactory 的原生实现（App 启动时注入 Core 侧消费者）。
    // K03：持有与 SessionManager 共用的 NativeSshAgent，多会话复用同一份
    // native 托管内存。
    public sealed class NativeSshSessionFactory : ISshSessionFactory
    {
        private readonly NativeSshAgent _agent;
        private readonly SettingsRepository _settings;

        public NativeSshSessionFactory()
            : this(null, null)
        {
        }

        public NativeSshSessionFactory(NativeSshAgent agent)
            : this(agent, null)
        {
        }

        // fix/functional-pass（P1-4）：settings 非空时每个新会话按 scrollbackLines 设定回滚容量
        // （此前设置项只存不用，native 恒为默认 5000 行）。
        public NativeSshSessionFactory(NativeSshAgent agent, SettingsRepository settings)
        {
            _agent = agent;
            _settings = settings;
        }

        public ISshSession Create()
        {
            int scrollback = 0;
            if (_settings != null)
            {
                try
                {
                    scrollback = _settings.ScrollbackLines;
                }
                catch (System.Exception)
                {
                    scrollback = 0;
                }
            }
            return new NativeSshSession(_agent, scrollback);
        }
    }
}
