namespace SshTool.Core.Common
{
    // 01-DESIGN.md §12.2：所有实现写盘前必须过 LogRedactor。
    public interface ILogger
    {
        void Log(LogLevel level, string tag, string message);
    }
}
