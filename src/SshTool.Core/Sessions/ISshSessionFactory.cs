namespace SshTool.Core.Sessions
{
    // N09b：会话工厂（01-DESIGN §4.1：Core 不引用 Native，由 App 注入
    // NativeSshSessionFactory；测试注入 FakeSshSession）。
    public interface ISshSessionFactory
    {
        ISshSession Create();
    }
}
