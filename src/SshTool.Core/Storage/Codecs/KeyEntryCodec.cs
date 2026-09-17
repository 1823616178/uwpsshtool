using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SshTool.Core.Models;

namespace SshTool.Core.Storage.Codecs
{
    // §8.1 KeyEntry（仅元数据；私钥内容在 SecretStore）
    public sealed class KeyEntryCodec : IEntityCodec<KeyEntry>
    {
        private static readonly ISet<string> KnownKeys = new HashSet<string>(System.StringComparer.Ordinal)
        {
            "id", "name", "keyType", "bits", "format", "encrypted",
            "publicKeyOpenSsh", "fingerprintSha256", "createdAt", "comment"
        };

        public string GetId(KeyEntry item)
        {
            return item.Id;
        }

        public JObject Encode(KeyEntry item)
        {
            var o = new JObject();
            o.Set("id", item.Id);
            o.Set("name", item.Name);
            o.Set("keyType", item.KeyType);
            o.Set("bits", item.Bits);
            o.Set("format", item.Format);
            o.Set("encrypted", item.Encrypted);
            o.Set("publicKeyOpenSsh", item.PublicKeyOpenSsh);
            o.Set("fingerprintSha256", item.FingerprintSha256);
            o.Set("createdAt", item.CreatedAt);
            o.Set("comment", item.Comment);
            o.MergeExtra(item.Extra);
            return o;
        }

        public KeyEntry Decode(JObject json)
        {
            var item = new KeyEntry();
            item.Id = json.GetString("id");
            item.Name = json.GetString("name");
            item.KeyType = json.GetString("keyType");
            item.Bits = json.GetInt("bits");
            item.Format = json.GetString("format");
            item.Encrypted = json.GetBool("encrypted");
            item.PublicKeyOpenSsh = json.GetString("publicKeyOpenSsh");
            item.FingerprintSha256 = json.GetString("fingerprintSha256");
            item.CreatedAt = json.GetString("createdAt");
            item.Comment = json.GetString("comment");
            item.Extra = json.ExtractExtra(KnownKeys);
            return item;
        }
    }
}
