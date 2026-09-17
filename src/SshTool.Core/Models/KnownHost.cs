using Newtonsoft.Json.Linq;

namespace SshTool.Core.Models
{
    // 01-DESIGN.md §8.1 KnownHost 🏠：与 Host.hostFingerprint（☁）的优先级见设计注释。
    public sealed class KnownHost
    {
        public string Id { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public string KeyType { get; set; }
        public string FingerprintSha256 { get; set; }
        public string AddedAt { get; set; }       // ISO 时间
        public string LastSeenAt { get; set; }    // ISO 时间

        public JObject Extra { get; set; }

        public KnownHost Clone()
        {
            return new KnownHost
            {
                Id = Id,
                Host = Host,
                Port = Port,
                KeyType = KeyType,
                FingerprintSha256 = FingerprintSha256,
                AddedAt = AddedAt,
                LastSeenAt = LastSeenAt,
                Extra = Extra == null ? null : (JObject)Extra.DeepClone()
            };
        }
    }
}
