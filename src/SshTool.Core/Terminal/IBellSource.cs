namespace SshTool.Core.Terminal
{
    // W04（01-DESIGN §16.4）：能报告累计 BEL 次数的屏幕（原生屏幕实现；测试替身与静态屏幕不必实现）。
    public interface IBellSource
    {
        long BellCount { get; }
    }
}
