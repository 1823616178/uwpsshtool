namespace SshTool.App.Infrastructure
{
    // W03/W04（01-DESIGN §16.3、§16.4）：激活参数（磁贴 / ssh:// 链接 / 断线通知）到主页的一次性交接。
    // App 在激活时写入，MainPage 导航进来时取走；取走即清空，返回主页不会重复触发。
    // 三种请求互斥，后写入的覆盖先写入的。
    public sealed class LaunchRequest
    {
        public string HostId { get; set; }
        public string QuickConnectText { get; set; }
        public string SessionId { get; set; }
    }

    public static class LaunchRequests
    {
        private static readonly object Gate = new object();
        private static LaunchRequest _pending;

        public static void RequestHost(string hostId)
        {
            Set(new LaunchRequest { HostId = hostId });
        }

        public static void RequestQuickConnect(string text)
        {
            Set(new LaunchRequest { QuickConnectText = text });
        }

        public static void RequestSession(string sessionId)
        {
            Set(new LaunchRequest { SessionId = sessionId });
        }

        public static LaunchRequest Take()
        {
            lock (Gate)
            {
                LaunchRequest pending = _pending;
                _pending = null;
                return pending;
            }
        }

        private static void Set(LaunchRequest request)
        {
            lock (Gate)
            {
                _pending = request;
            }
        }
    }
}
