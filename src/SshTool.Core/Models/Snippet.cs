using Newtonsoft.Json.Linq;

namespace SshTool.Core.Models
{
    // 01-DESIGN.md §8.1 Snippet 🏠
    public sealed class Snippet
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Content { get; set; }
        public string GroupName { get; set; }    // 简单文本分组
        public int SortOrder { get; set; }
        public bool SendEnter { get; set; }      // 发送后是否追加回车

        public JObject Extra { get; set; }

        public Snippet Clone()
        {
            return new Snippet
            {
                Id = Id,
                Name = Name,
                Content = Content,
                GroupName = GroupName,
                SortOrder = SortOrder,
                SendEnter = SendEnter,
                Extra = Extra == null ? null : (JObject)Extra.DeepClone()
            };
        }
    }
}
