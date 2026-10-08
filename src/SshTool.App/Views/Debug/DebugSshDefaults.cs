namespace SshTool.App.Views.Debug
{
    // 仅供 DEBUG_PAGES / 真机回归使用。不要用于正式主机配置，也不要写入日志。
    //
    // 安全：测试服务器的地址/账号/密码**不入库**。值来自本机私有文件
    // `DebugSshDefaults.local.cs`（已 .gitignore；模板见同目录
    // `DebugSshDefaults.local.cs.example`），由 csproj 在文件存在时才编译进来。
    // 文件不存在时全部为空串 / 22 端口，调试页输入框留空，手动填写即可。
    internal static partial class DebugSshDefaults
    {
        public static readonly string Host;
        public static readonly int Port;
        public static readonly string PortText;
        public static readonly string User;
        public static readonly string Password;

        static DebugSshDefaults()
        {
            string host = string.Empty;
            int port = 22;
            string user = string.Empty;
            string password = string.Empty;
            LoadLocal(ref host, ref port, ref user, ref password);
            Host = host ?? string.Empty;
            Port = port > 0 && port <= 65535 ? port : 22;
            PortText = Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            User = user ?? string.Empty;
            Password = password ?? string.Empty;
        }

        // 由 DebugSshDefaults.local.cs 实现；未提供时编译器移除调用。
        static partial void LoadLocal(ref string host, ref int port, ref string user, ref string password);
    }
}
