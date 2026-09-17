using SshTool.Core.Models;

namespace SshTool.Core.Sync.Protocol
{
    // §4.1 servers[].profile：主机的 8 个可移植字段（与桌面端 PortableServerProfile 同构）。
    public sealed class PortableServerProfile
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public string Username { get; set; }
        public AuthType AuthType { get; set; }
        public string HostFingerprint { get; set; }
        public int Keepalive { get; set; }

        public PortableServerProfile Clone()
        {
            return new PortableServerProfile
            {
                Id = Id,
                Name = Name,
                Host = Host,
                Port = Port,
                Username = Username,
                AuthType = AuthType,
                HostFingerprint = HostFingerprint,
                Keepalive = Keepalive
            };
        }
    }
}
