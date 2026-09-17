using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SshTool.Core.Models;

namespace SshTool.Core.Storage.Codecs
{
    // §8.1 HostGroup
    public sealed class HostGroupCodec : IEntityCodec<HostGroup>
    {
        private static readonly ISet<string> KnownKeys = new HashSet<string>(System.StringComparer.Ordinal)
        {
            "id", "name", "color", "order", "collapsed"
        };

        public string GetId(HostGroup item)
        {
            return item.Id;
        }

        public JObject Encode(HostGroup item)
        {
            var o = new JObject();
            o.Set("id", item.Id);
            o.Set("name", item.Name);
            o.Set("color", item.Color);
            o.Set("order", item.Order);
            o.Set("collapsed", item.Collapsed);
            o.MergeExtra(item.Extra);
            return o;
        }

        public HostGroup Decode(JObject json)
        {
            var item = new HostGroup();
            item.Id = json.GetString("id");
            item.Name = json.GetString("name");
            item.Color = json.GetString("color");
            item.Order = json.GetInt("order");
            item.Collapsed = json.GetBool("collapsed");
            item.Extra = json.ExtractExtra(KnownKeys);
            return item;
        }
    }
}
