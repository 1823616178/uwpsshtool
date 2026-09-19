using System;
using System.Threading.Tasks;

namespace SshTool.Core.Storage
{
    // 01-DESIGN.md §8.2：凭据键值表（DPAPI 加密实现由 D03 提供）。
    //
    // S14：落盘后触发 Changed（事件只带键名与来源，不带值）。
    // origin 语义与 Repository 一致：User = 用户/界面侧写入 → 同步标脏；
    // Sync = 同步下行写入（SyncLocalAdapter.ApplyDocumentAsync）→ 不标脏，避免上传回环。
    public interface ISecretStore
    {
        event EventHandler<SecretChangedEventArgs> Changed;

        Task<string> GetAsync(string key);
        Task SetAsync(string key, string value, ChangeOrigin origin = ChangeOrigin.User);
        Task RemoveAsync(string key, ChangeOrigin origin = ChangeOrigin.User);
        Task RemoveByPrefixAsync(string prefix, ChangeOrigin origin = ChangeOrigin.User);
    }
}
