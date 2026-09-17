using SshTool.Core.Models;

namespace SshTool.Core.Sync.Protocol
{
    // §4.1 tunnels[]：与桌面端 PortableTunnelConfig（= TunnelConfig 去掉 autoStart）同构。
    public sealed class TunnelRecord
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string ServerId { get; set; }
        public string GroupId { get; set; }   // null 或非空字符串（指向 groups）
        public TunnelType Type { get; set; }
        public string ListenHost { get; set; }
        public int ListenPort { get; set; }
        public string DestHost { get; set; }
        public int DestPort { get; set; }
        public string DestServerId { get; set; }
        public bool AutoReconnect { get; set; }
        public bool Enabled { get; set; }

        public TunnelRecord Clone()
        {
            return new TunnelRecord
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
                Enabled = Enabled
            };
        }
    }
}
