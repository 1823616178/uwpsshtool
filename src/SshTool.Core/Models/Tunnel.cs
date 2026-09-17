using Newtonsoft.Json.Linq;

namespace SshTool.Core.Models
{
    // 01-DESIGN.md §8.1 Tunnel（与桌面端 TunnelConfig 同构）。
    public sealed class Tunnel
    {
        // ☁
        public string Id { get; set; }
        public string Name { get; set; }              // 1–255
        public string ServerId { get; set; }          // 指向 Host.Id
        public string GroupId { get; set; }           // 可空；非空须指向存在的分组
        public TunnelType Type { get; set; }
        public string ListenHost { get; set; }        // ≤1024
        public int ListenPort { get; set; }           // 1–65535
        public string DestHost { get; set; }          // ≤1024；dynamic 不需要
        public int DestPort { get; set; }             // 0–65535
        public string DestServerId { get; set; }      // ≤255；relay 必填且指向存在的主机
        public bool AutoReconnect { get; set; }
        public bool Enabled { get; set; }

        // 🏠
        public bool AutoStart { get; set; }           // 手机默认 false

        public JObject Extra { get; set; }

        public Tunnel Clone()
        {
            return new Tunnel
            {
                Id = Id,
                Name = Name,
                ServerId = ServerId,
                GroupId = GroupId,
                Type = Type,
                ListenHost = ListenHost,
                ListenPort = ListenPort,
                DestHost = DestHost,
                DestPort = DestPort,
                DestServerId = DestServerId,
                AutoReconnect = AutoReconnect,
                Enabled = Enabled,
                AutoStart = AutoStart,
                Extra = Extra == null ? null : (JObject)Extra.DeepClone()
            };
        }
    }
}
