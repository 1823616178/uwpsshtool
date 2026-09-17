using Newtonsoft.Json.Linq;

namespace SshTool.Core.Models
{
    // 01-DESIGN.md §8.1 KeyEntry 🏠：私钥内容存 SecretStore，这里只有元数据。
    public sealed class KeyEntry
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string KeyType { get; set; }           // ssh-ed25519 / ssh-rsa / ecdsa-sha2-nistp256 …
        public int Bits { get; set; }
        public string Format { get; set; }            // openssh / pem
        public bool Encrypted { get; set; }
        public string PublicKeyOpenSsh { get; set; }
        public string FingerprintSha256 { get; set; }
        public string CreatedAt { get; set; }         // ISO 时间
        public string Comment { get; set; }

        public JObject Extra { get; set; }

        public KeyEntry Clone()
        {
            return new KeyEntry
            {
                Id = Id,
                Name = Name,
                KeyType = KeyType,
                Bits = Bits,
                Format = Format,
                Encrypted = Encrypted,
                PublicKeyOpenSsh = PublicKeyOpenSsh,
                FingerprintSha256 = FingerprintSha256,
                CreatedAt = CreatedAt,
                Comment = Comment,
                Extra = Extra == null ? null : (JObject)Extra.DeepClone()
            };
        }
    }
}
