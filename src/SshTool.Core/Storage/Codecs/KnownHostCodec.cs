using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SshTool.Core.Models;

namespace SshTool.Core.Storage.Codecs
{
    // §8.1 KnownHost
    public sealed class KnownHostCodec : IEntityCodec<KnownHost>
    {
        private static readonly ISet<string> KnownKeys = new HashSet<string>(System.StringComparer.Ordinal)
        {
            "id", "host", "port", "keyType", "fingerprintSha256", "addedAt", "lastSeenAt"
        };

        public string GetId(KnownHost item)
        {
            return item.Id;
        }

        public JObject Encode(KnownHost item)
        {
            var o = new JObject();
            o.Set("id", item.Id);
            o.Set("host", item.Host);
            o.Set("port", item.Port);
            o.Set("keyType", item.KeyType);
            o.Set("fingerprintSha256", item.FingerprintSha256);
            o.Set("addedAt", item.AddedAt);
            o.Set("lastSeenAt", item.LastSeenAt);
            o.MergeExtra(item.Extra);
            return o;
        }

        public KnownHost Decode(JObject json)
        {
            var item = new KnownHost();
            item.Id = json.GetString("id");
            item.Host = json.GetString("host");
            item.Port = json.GetInt("port");
            item.KeyType = json.GetString("keyType");
            item.FingerprintSha256 = json.GetString("fingerprintSha256");
            item.AddedAt = json.GetString("addedAt");
            item.LastSeenAt = json.GetString("lastSeenAt");
            item.Extra = json.ExtractExtra(KnownKeys);
            return item;
        }
    }
}
