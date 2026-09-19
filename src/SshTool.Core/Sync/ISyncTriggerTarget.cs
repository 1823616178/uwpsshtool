using System.Threading.Tasks;

namespace SshTool.Core.Sync
{
    // S14 同步触发器眼中的协调器窄接口（03-SYNC-PROTOCOL.md §7.5）。
    // SyncCoordinator 已隐式实现全部三项（CurrentState/无参 SyncNowAsync 见其 S14 段注释）；
    // 单测用轻量假实现，避免为触发器逻辑组装整套 Api/加密/适配器。
    public interface ISyncTriggerTarget
    {
        // 当前 UI 可观察状态（拷贝；含 Preferences.Enabled/AutoSync 与 LastSyncedAt）。
        SyncState CurrentState { get; }

        // 本地修改标脏（含 MarkDirtyDebounceMs 防抖调度，见 SyncCoordinator.MarkDirtyAsync）。
        Task MarkDirtyAsync();

        // 单飞同步（见 SyncCoordinator.SyncNowAsync；调用方自行决定是否等待结果）。
        Task SyncNowAsync();
    }
}
