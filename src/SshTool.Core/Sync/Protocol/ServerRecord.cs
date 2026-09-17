namespace SshTool.Core.Sync.Protocol
{
    // §4.1 servers[] 条目：profile 必有；secrets 为 null 表示该主机不带 secrets 键。
    public sealed class ServerRecord
    {
        public PortableServerProfile Profile { get; set; }
        public ServerSecrets Secrets { get; set; }

        public ServerRecord Clone()
        {
            return new ServerRecord
            {
                Profile = Profile == null ? null : Profile.Clone(),
                Secrets = Secrets == null ? null : Secrets.Clone()
            };
        }
    }
}
