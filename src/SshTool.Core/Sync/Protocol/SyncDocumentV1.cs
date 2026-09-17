using System.Collections.Generic;

namespace SshTool.Core.Sync.Protocol
{
    // §4.1 SyncDocumentV1：三端共用同一份文档。updatedAt 为 toISOString 形式
    // （yyyy-MM-ddTHH:mm:ss.fffZ，见 §4.2 与踩坑 7）。
    public sealed class SyncDocumentV1
    {
        public int SchemaVersion { get; set; } = SyncConstants.SchemaVersion;
        public string UpdatedAt { get; set; }
        public SyncPreferencesV1 Preferences { get; set; } = new SyncPreferencesV1();
        public List<ServerRecord> Servers { get; set; } = new List<ServerRecord>();
        public List<TunnelRecord> Tunnels { get; set; } = new List<TunnelRecord>();
        public List<GroupRecord> Groups { get; set; } = new List<GroupRecord>();

        public SyncDocumentV1 Clone()
        {
            var copy = new SyncDocumentV1
            {
                SchemaVersion = SchemaVersion,
                UpdatedAt = UpdatedAt,
                Preferences = Preferences == null ? null : Preferences.Clone(),
                Servers = new List<ServerRecord>(Servers.Count),
                Tunnels = new List<TunnelRecord>(Tunnels.Count),
                Groups = new List<GroupRecord>(Groups.Count)
            };
            foreach (var s in Servers)
            {
                copy.Servers.Add(s == null ? null : s.Clone());
            }
            foreach (var t in Tunnels)
            {
                copy.Tunnels.Add(t == null ? null : t.Clone());
            }
            foreach (var g in Groups)
            {
                copy.Groups.Add(g == null ? null : g.Clone());
            }
            return copy;
        }
    }
}
