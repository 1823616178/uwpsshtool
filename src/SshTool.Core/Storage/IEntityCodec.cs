using Newtonsoft.Json.Linq;

namespace SshTool.Core.Storage
{
    // 01-DESIGN.md §8.4/D9：手写 JObject 编解码（禁止反射式序列化）。
    // 解码时未知字段收进实体 Extra，编码时 Extra 追加在已知字段之后（固定键序）。
    public interface IEntityCodec<T>
    {
        string GetId(T item);
        JObject Encode(T item);
        T Decode(JObject json);
    }
}
