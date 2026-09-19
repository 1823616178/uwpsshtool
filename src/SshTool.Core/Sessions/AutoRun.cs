using System.Collections.Generic;
using System.Text;
using SshTool.Core.Models;

namespace SshTool.Core.Sessions
{
    // 01-DESIGN.md §9.4：连接（含重连）后自动执行——tmux 附着 + 初始命令。
    // 纯函数，发送与记日志由 SessionManager 负责（日志只记条数）。
    public static class AutoRun
    {
        public const string DefaultTmuxSessionName = "main";

        public static IList<string> BuildCommands(Host host)
        {
            var list = new List<string>();
            if (host == null)
            {
                return list;
            }
            if (host.TmuxAutoAttach)
            {
                list.Add("tmux new-session -A -s " + SanitizeTmuxName(host.TmuxSessionName));
            }
            if (host.InitCommands != null)
            {
                for (int i = 0; i < host.InitCommands.Count; i++)
                {
                    string cmd = host.InitCommands[i];
                    if (!string.IsNullOrWhiteSpace(cmd))
                    {
                        list.Add(cmd);
                    }
                }
            }
            return list;
        }

        // tmux 会话名只保留 [A-Za-z0-9_.-]，其余替换为 _；空名用 main。
        public static string SanitizeTmuxName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return DefaultTmuxSessionName;
            }
            var sb = new StringBuilder(name.Length);
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                    || (c >= '0' && c <= '9') || c == '_' || c == '.' || c == '-';
                sb.Append(ok ? c : '_');
            }
            return sb.ToString();
        }
    }
}
