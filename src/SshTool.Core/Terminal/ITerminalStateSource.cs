namespace SshTool.Core.Terminal
{
    // opt/full-pass：能一次加锁读完整帧状态的屏幕（原生屏幕实现；测试替身与静态屏幕不必实现，
    // TerminalScreenState.TryRead 会回退到逐属性读取）。
    public interface ITerminalStateSource
    {
        // 锁忙时立即返回 false（不阻塞 UI 线程）。
        bool TryReadState(out TerminalScreenState state);
    }
}
