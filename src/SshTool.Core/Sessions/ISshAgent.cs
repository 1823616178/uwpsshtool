using System.Threading.Tasks;

namespace SshTool.Core.Sessions
{
    // K03：应用内 Agent 的 Core 侧抽象（01-DESIGN.md §4.1：Core 不引用 Native，
    // 由 App 层注入 NativeSshAgent 实现；单测注入 FakeSshAgent）。
    //
    // 契约（对齐 native Bridge.SshAgent，见 native/core/ssh/agent.h）：
    //   - 私钥明文只进 native 内存：Unlock 把材料送入后 C# 侧立即丢引用；
    //     认证经 ISshSession.AuthenticateAgentAsync(keyId) 在 native 内取钥，
    //     私钥永不回流到 C#；
    //   - 超时是惰性语义（每次调用时判定），0 = 永不超时；C# 层按设置项
    //     agentKeyTimeoutMinutes（默认 15）驱动 SetTimeout；
    //   - 实现与调用方均不得记录私钥、短语内容（日志脱敏；最多记 keyId 与计数）。
    public interface ISshAgent
    {
        // 解锁并托管（keyId 采用 KeyEntry id）。格式粗检不通过返回 false。
        // 短语无则传空串（不传 null）。
        Task<bool> UnlockAsync(string keyId, string privateKeyText, string passphrase);

        // 手动锁定指定条目；false = 不存在（含已被超时惰性清除）。
        bool Lock(string keyId);

        // 锁定全部（幂等）：超时到期、应用挂起时清除共用此入口。
        void LockAll();

        // 托管超时（分钟，0 = 永不超时；负数按 0 处理）。
        void SetTimeout(int minutes);

        int TimeoutMinutes { get; }

        // keyId 当前是否锁定（不存在/已清除/已超时 → true）。
        bool IsLocked(string keyId);

        int KeyCount { get; }
    }
}
