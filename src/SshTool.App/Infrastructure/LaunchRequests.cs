namespace SshTool.App.Infrastructure
{
    // W03（01-DESIGN §16.3）：激活参数（磁贴 / ssh:// 链接）到主页的一次性交接。
    // App 在激活时写入，MainPage 导航进来时取走；取走即清空，返回主页不会重复触发。
    public static class LaunchRequests
    {
        private static readonly object Gate = new object();
        private static string _hostId;
        private static string _quickConnectText;

        public static void RequestHost(string hostId)
        {
            lock (Gate)
            {
                _hostId = hostId;
                _quickConnectText = null;
            }
        }

        public static void RequestQuickConnect(string text)
        {
            lock (Gate)
            {
                _quickConnectText = text;
                _hostId = null;
            }
        }

        public static bool TryTake(out string hostId, out string quickConnectText)
        {
            lock (Gate)
            {
                hostId = _hostId;
                quickConnectText = _quickConnectText;
                _hostId = null;
                _quickConnectText = null;
                return hostId != null || quickConnectText != null;
            }
        }
    }
}
