using System;

namespace SshTool.Core.Hosts
{
    // W03（01-DESIGN §16.3）：开始屏幕磁贴的激活参数与 ssh:// 协议链接。纯函数。
    public static class HostLaunchLinks
    {
        public const string TileArgumentPrefix = "host:";

        public static string TileArguments(string hostId)
        {
            return TileArgumentPrefix + (hostId ?? string.Empty);
        }

        public static bool TryParseTileArguments(string arguments, out string hostId)
        {
            hostId = null;
            if (string.IsNullOrEmpty(arguments)
                || !arguments.StartsWith(TileArgumentPrefix, StringComparison.Ordinal)
                || arguments.Length == TileArgumentPrefix.Length)
            {
                return false;
            }
            hostId = arguments.Substring(TileArgumentPrefix.Length);
            return true;
        }

        // SecondaryTile 的 TileId：< 64 字符，只允许字母、数字、点、下划线，且以字母或数字开头。
        // 主机 id 只用于区分磁贴，真实 id 走 Arguments，所以有损替换无妨。
        public static string TileId(string hostId)
        {
            string id = hostId ?? string.Empty;
            var chars = new char[id.Length];
            for (int i = 0; i < id.Length; i++)
            {
                char c = id[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '_';
                chars[i] = ok ? c : '_';
            }
            string tile = "host_" + new string(chars);
            return tile.Length > 63 ? tile.Substring(0, 63) : tile;
        }

        // ssh://[user[;参数]@]host[:port][/...] → 快速连接输入框文本（user@host:port 或 host:port）。
        // 不自动连接：只预填，避免网页一键把用户送进登录流程。
        public static bool TryParseSshUri(string uri, out string quickConnectText)
        {
            quickConnectText = null;
            if (string.IsNullOrWhiteSpace(uri))
            {
                return false;
            }
            string text = uri.Trim();
            const string scheme = "ssh://";
            if (!text.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            string rest = text.Substring(scheme.Length);
            int cut = rest.IndexOfAny(new[] { '/', '?', '#' });
            if (cut >= 0)
            {
                rest = rest.Substring(0, cut);
            }
            string user = string.Empty;
            int at = rest.LastIndexOf('@');
            if (at >= 0)
            {
                user = rest.Substring(0, at);
                rest = rest.Substring(at + 1);
                int semicolon = user.IndexOf(';');
                if (semicolon >= 0)
                {
                    user = user.Substring(0, semicolon); // ssh URI 草案里的 ;fingerprint=… 参数
                }
                user = Uri.UnescapeDataString(user);
            }
            if (rest.Length == 0 || user.IndexOfAny(new[] { ' ', '@' }) >= 0)
            {
                return false;
            }
            string host = rest;
            string port = string.Empty;
            if (rest[0] == '[')
            {
                int close = rest.IndexOf(']');
                if (close <= 1)
                {
                    return false;
                }
                host = rest.Substring(0, close + 1);
                string after = rest.Substring(close + 1);
                if (after.Length > 0)
                {
                    if (after[0] != ':')
                    {
                        return false;
                    }
                    port = after.Substring(1);
                }
            }
            else
            {
                int colon = rest.LastIndexOf(':');
                if (colon >= 0 && rest.IndexOf(':') == colon)
                {
                    host = rest.Substring(0, colon);
                    port = rest.Substring(colon + 1);
                }
            }
            if (host.Length == 0 || host.IndexOf(' ') >= 0)
            {
                return false;
            }
            if (port.Length > 0)
            {
                int value;
                if (!int.TryParse(port, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out value) || value < 1 || value > 65535)
                {
                    return false;
                }
            }
            string target = host + (port.Length > 0 ? ":" + port : string.Empty);
            quickConnectText = user.Length > 0 ? user + "@" + target : target;
            return true;
        }
    }
}
