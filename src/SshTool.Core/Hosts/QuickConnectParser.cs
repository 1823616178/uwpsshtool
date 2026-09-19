namespace SshTool.Core.Hosts
{
    public sealed class QuickConnectTarget
    {
        public QuickConnectTarget(string username, string hostName, int port)
        {
            Username = username;
            HostName = hostName;
            Port = port;
        }

        public string Username { get; private set; }
        public string HostName { get; private set; }
        public int Port { get; private set; }
    }

    // 02-UI-DESIGN.md §5.1：user@host[:port]，IPv6 用 [addr] 或无端口的字面量。
    public static class QuickConnectParser
    {
        public static bool TryParse(string input, out QuickConnectTarget target)
        {
            target = null;
            if (string.IsNullOrWhiteSpace(input))
            {
                return false;
            }
            string trimmed = input.Trim();
            int at = trimmed.LastIndexOf('@');
            if (at <= 0 || at == trimmed.Length - 1)
            {
                return false;
            }
            string user = trimmed.Substring(0, at).Trim();
            string rest = trimmed.Substring(at + 1).Trim();
            if (user.Length == 0 || rest.Length == 0)
            {
                return false;
            }

            string host;
            int port;
            if (rest[0] == '[')
            {
                int close = rest.IndexOf(']');
                if (close <= 1)
                {
                    return false;
                }
                host = rest.Substring(1, close - 1).Trim();
                if (host.Length == 0)
                {
                    return false;
                }
                string after = rest.Substring(close + 1).Trim();
                if (after.Length == 0)
                {
                    port = 22;
                }
                else if (after[0] == ':')
                {
                    if (!TryParsePort(after.Substring(1).Trim(), out port))
                    {
                        return false;
                    }
                }
                else
                {
                    return false;
                }
            }
            else
            {
                int firstColon = rest.IndexOf(':');
                int lastColon = rest.LastIndexOf(':');
                if (firstColon < 0)
                {
                    host = rest;
                    port = 22;
                }
                else if (firstColon != lastColon)
                {
                    // 未加括号的 IPv6，不能再带端口。
                    host = rest;
                    port = 22;
                }
                else
                {
                    host = rest.Substring(0, firstColon).Trim();
                    if (host.Length == 0)
                    {
                        return false;
                    }
                    if (!TryParsePort(rest.Substring(firstColon + 1).Trim(), out port))
                    {
                        return false;
                    }
                }
            }

            if (host.Length == 0)
            {
                return false;
            }
            target = new QuickConnectTarget(user, host, port);
            return true;
        }

        private static bool TryParsePort(string text, out int port)
        {
            port = 0;
            if (string.IsNullOrEmpty(text) || text.Length > 5)
            {
                return false;
            }
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] < '0' || text[i] > '9')
                {
                    return false;
                }
            }
            int value = 0;
            for (int i = 0; i < text.Length; i++)
            {
                value = (value * 10) + (text[i] - '0');
            }
            if (value < 1 || value > 65535)
            {
                return false;
            }
            port = value;
            return true;
        }
    }
}
