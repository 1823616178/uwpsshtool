using System;
using SshTool.App.Infrastructure;
using SshTool.Core.Sync;
using SshTool.Core.Sync.Auth;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Views.Sync
{
    // U16：登录/建库/解锁后的统一去向（02-UI-DESIGN.md §5.13 状态 Pivot 的过渡路由）。
    //   未登录                         → LoginPage（fix/login-feedback 补上；此前从不去登录页）；
    //   vault missing（账号从未建库）→ VaultSetupPage；
    //   vault locked（已建库未解锁）  → VaultUnlockPage；
    //   其余（ready）                 → AccountSyncPage。
    // 判定本身在 Core 的 SyncRouting（可单测）。fix/login-feedback 之前这里只看 State.Vault，
    // 而未登录时 Vault 恒为 Missing：「设置 › 账号与同步」/ 主页同步图标进入 AccountSyncPage 后
    // 被转去建库页，登录页根本到不了；同步栈缺失时则转回 AccountSyncPage 自己（同页重入）。
    // U15 LoginPage 原来的 GoStatusPlaceholder 固定跳占位页，且完成后中间页留在返回栈里，
    // 按返回键会弹回已失效的登录页形成循环；本助手在导航后从返回栈剪掉失效中间页
    //（剪到哪由页面类型判定，见下），保证返回键直达上一个仍有效的页面。
    internal static class SyncNavigation
    {
        // 去向目标页。prune 形参保留以兼容既有调用点（LoginPage/VaultSetupPage/VaultUnlockPage
        // 传的是各自流程的「预期条数」），实际剪栈已改由类型判定，不再受这个数限制：
        // 固定条数会越剪——建库完成时登录页早已剪掉，按 prune=2 剪会把主页一并剪没；
        // 也会漏剪——解锁页走「已在栈里有登录页」的分支时 prune=1 只剪掉解锁页本身。
        // 返回是否真的发起了导航：目标就是当前页时不导航（防同页重入循环），调用方据此继续渲染本页。
        public static bool GoAfterAuth(Frame frame, int prune)
        {
            return GoAfterAuth(frame, prune, false);
        }

        // dropSource：调用方本身只是「中转」（AccountSyncPage 发现需要先登录/建库/解锁而转走），
        // 导航后把它从返回栈里拿掉——否则登录完成后返回键会回到一个旧的状态页副本。
        public static bool GoAfterAuth(Frame frame, int prune, bool dropSource)
        {
            if (frame == null)
            {
                return false;
            }
            Type current = frame.CurrentSourcePageType;
            SyncScreenKind target = Decide();
            if (!SyncRouting.ShouldNavigate(KindOf(current), target))
            {
                return false;
            }
            Type targetPage = PageOf(target);
            if (!frame.Navigate(targetPage, null))
            {
                return false;
            }
            if (dropSource && current != null && frame.BackStack.Count > 0
                && frame.BackStack[frame.BackStack.Count - 1].SourcePageType == current)
            {
                frame.BackStack.RemoveAt(frame.BackStack.Count - 1);
            }
            // Navigate 只把「上一页」压进返回栈（当前页不入栈），所以栈顶就是刚离开的
            // 登录/建库/解锁页。逐个剪掉这类失效中间页，遇到任何其它类型（主页、终端页…）
            // 立即停止：登录→建库→完成这条路径剪完后栈顶仍是主页，主页留在栈内。
            // feat/account-sync-ui：与目标同类的旧页一并剪掉——状态页现在会留在栈里（保险库卡
            // 「输入同步密码解锁」→ 解锁页 → 完成后去新的状态页），不剪的话返回键会先回到旧状态页。
            while (frame.BackStack.Count > 0)
            {
                Type top = frame.BackStack[frame.BackStack.Count - 1].SourcePageType;
                if (!IsStaleAuthPage(top) && top != targetPage)
                {
                    break;
                }
                frame.BackStack.RemoveAt(frame.BackStack.Count - 1);
            }
            return true;
        }

        // 认证流程中间页：完成一次跳转后就不能再用（回去必被顶出来，形成返回循环）。
        private static bool IsStaleAuthPage(Type pageType)
        {
            return pageType == typeof(LoginPage)
                || pageType == typeof(VaultSetupPage)
                || pageType == typeof(VaultUnlockPage);
        }

        private static Type PageOf(SyncScreenKind kind)
        {
            switch (kind)
            {
                case SyncScreenKind.Login:
                    return typeof(LoginPage);
                case SyncScreenKind.CreateVault:
                    return typeof(VaultSetupPage);
                case SyncScreenKind.UnlockVault:
                    return typeof(VaultUnlockPage);
                default:
                    return typeof(AccountSyncPage);
            }
        }

        private static SyncScreenKind? KindOf(Type pageType)
        {
            if (pageType == typeof(LoginPage))
            {
                return SyncScreenKind.Login;
            }
            if (pageType == typeof(VaultSetupPage))
            {
                return SyncScreenKind.CreateVault;
            }
            if (pageType == typeof(VaultUnlockPage))
            {
                return SyncScreenKind.UnlockVault;
            }
            if (pageType == typeof(AccountSyncPage))
            {
                return SyncScreenKind.Status;
            }
            return null;
        }

        // 会话读自 AuthStore（与 LoginPage.IsSignedIn、AccountSyncViewModel 经 AuthService 读的是同一份），
        // 保险库状态读自 Coordinator 快照（登录成功时 AfterAuthenticated 已重建并探测过，是新鲜的）。
        // 读不到同步栈时按「不可用」处理（去登录页，提交时提示同步组件不可用）。
        private static SyncScreenKind Decide()
        {
            try
            {
                AppServices services = AppServices.Current;
                if (services != null && services.Sync != null)
                {
                    AuthState auth = services.Auth != null ? services.Auth.Session : null;
                    return SyncRouting.Decide(true, auth, services.Sync.State);
                }
            }
            catch (Exception)
            {
            }
            return SyncRouting.Decide(false, null, null);
        }
    }
}
