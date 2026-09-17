namespace SshTool.Core.Sync.Protocol
{
    // §4.1 groups[]：仅 id/name/color 进文档（order/collapsed 本机专有）。
    public sealed class GroupRecord
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Color { get; set; }   // ^#[0-9a-fA-F]{6}$

        public GroupRecord Clone()
        {
            return new GroupRecord
            {
                Id = Id,
                Name = Name,
                Color = Color
            };
        }
    }
}
