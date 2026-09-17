using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SshTool.Core.Models;

namespace SshTool.Core.Storage.Codecs
{
    // §8.1 Snippet
    public sealed class SnippetCodec : IEntityCodec<Snippet>
    {
        private static readonly ISet<string> KnownKeys = new HashSet<string>(System.StringComparer.Ordinal)
        {
            "id", "name", "content", "groupName", "sortOrder", "sendEnter"
        };

        public string GetId(Snippet item)
        {
            return item.Id;
        }

        public JObject Encode(Snippet item)
        {
            var o = new JObject();
            o.Set("id", item.Id);
            o.Set("name", item.Name);
            o.Set("content", item.Content);
            o.Set("groupName", item.GroupName);
            o.Set("sortOrder", item.SortOrder);
            o.Set("sendEnter", item.SendEnter);
            o.MergeExtra(item.Extra);
            return o;
        }

        public Snippet Decode(JObject json)
        {
            var item = new Snippet();
            item.Id = json.GetString("id");
            item.Name = json.GetString("name");
            item.Content = json.GetString("content");
            item.GroupName = json.GetString("groupName");
            item.SortOrder = json.GetInt("sortOrder");
            item.SendEnter = json.GetBool("sendEnter");
            item.Extra = json.ExtractExtra(KnownKeys);
            return item;
        }
    }
}
