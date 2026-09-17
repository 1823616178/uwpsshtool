using Newtonsoft.Json.Linq;

namespace SshTool.Core.Models
{
    // 01-DESIGN.md §8.1 HostGroup：本应用同时用于主机归属（host.groupId 🏠）。
    public sealed class HostGroup
    {
        public string Id { get; set; }        // ☁
        public string Name { get; set; }      // ☁ 1–255
        public string Color { get; set; }     // ☁ 必填 #RRGGBB，默认 #4F8CFF
        public int Order { get; set; }        // 🏠
        public bool Collapsed { get; set; }   // 🏠

        public JObject Extra { get; set; }

        public HostGroup Clone()
        {
            return new HostGroup
            {
                Id = Id,
                Name = Name,
                Color = Color,
                Order = Order,
                Collapsed = Collapsed,
                Extra = Extra == null ? null : (JObject)Extra.DeepClone()
            };
        }
    }
}
