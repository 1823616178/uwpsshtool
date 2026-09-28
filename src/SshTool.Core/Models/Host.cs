using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace SshTool.Core.Models
{
    // 01-DESIGN.md §8.1 Host。☁ 同步字段 / 🏠 本机专有见注释。
    public sealed class Host
    {
        // ☁
        public string Id { get; set; }
        public string Name { get; set; }
        public string HostName { get; set; }
        public int Port { get; set; }
        public string Username { get; set; }
        public AuthType AuthType { get; set; }
        public string HostFingerprint { get; set; }
        public int Keepalive { get; set; }

        // 🏠
        public string GroupId { get; set; }
        public string KeyId { get; set; }
        public string AppearanceId { get; set; }
        public string JumpHostId { get; set; }
        public List<string> InitCommands { get; set; } = new List<string>();
        public Dictionary<string, string> EnvVars { get; set; } = new Dictionary<string, string>();
        public string TermType { get; set; }
        public bool TmuxAutoAttach { get; set; }
        public string TmuxSessionName { get; set; }
        public bool BackspaceSendsCtrlH { get; set; }
        public int SortOrder { get; set; }
        public string LastConnectedAt { get; set; }
        // W03：本机专有（🏠），主机列表「收藏」段；不进同步文档。
        public bool Favorite { get; set; }

        // 编解码未知字段（D02 往返保留，未来兼容）
        public JObject Extra { get; set; }

        public Host Clone()
        {
            return new Host
            {
                Id = Id,
                Name = Name,
                HostName = HostName,
                Port = Port,
                Username = Username,
                AuthType = AuthType,
                HostFingerprint = HostFingerprint,
                Keepalive = Keepalive,
                GroupId = GroupId,
                KeyId = KeyId,
                AppearanceId = AppearanceId,
                JumpHostId = JumpHostId,
                InitCommands = new List<string>(InitCommands),
                EnvVars = new Dictionary<string, string>(EnvVars),
                TermType = TermType,
                TmuxAutoAttach = TmuxAutoAttach,
                TmuxSessionName = TmuxSessionName,
                BackspaceSendsCtrlH = BackspaceSendsCtrlH,
                SortOrder = SortOrder,
                LastConnectedAt = LastConnectedAt,
                Favorite = Favorite,
                Extra = Extra == null ? null : (JObject)Extra.DeepClone()
            };
        }
    }
}
