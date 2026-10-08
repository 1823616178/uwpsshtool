using SshTool.Core.Sync.Auth;

namespace SshTool.Core.Sync
{
    // fix/login-feedback：账号与同步各页（登录 / 建库 / 解锁 / 状态页）的统一去向判定（纯逻辑，可单测）。
    // 此前 App 的 SyncNavigation.GoAfterAuth 只看 SyncState.Vault，从不去登录页：
    // 未登录时 BuildState 恒给 Vault=Missing，于是「设置 › 账号与同步」/ 主页同步图标进入
    // AccountSyncPage 后被转去建库页（或在状态不可读时转回 AccountSyncPage 自己），登录页无从到达。
    // 现在统一走 SyncStatePresenter.DetermineScreen：未登录 → 登录；已登录按保险库状态。
    public static class SyncRouting
    {
        // syncAvailable=false（同步栈构造失败）时一律去登录页：登录页提交会给出
        // 「同步组件不可用」的明确提示，而状态页 / 建库页在没有同步栈时只会再把用户转走。
        public static SyncScreenKind Decide(bool syncAvailable, AuthState auth, SyncState state)
        {
            if (!syncAvailable)
            {
                return SyncScreenKind.Login;
            }
            return new SyncStatePresenter().DetermineScreen(auth, state);
        }

        // 目标就是当前页时不导航：同页重入会在 OnNavigatedTo 里再次判定并再次导航，形成循环。
        // current 为 null 表示当前页不属于同步流程（主页、设置页等），总是导航。
        public static bool ShouldNavigate(SyncScreenKind? current, SyncScreenKind target)
        {
            return !current.HasValue || current.Value != target;
        }
    }
}
