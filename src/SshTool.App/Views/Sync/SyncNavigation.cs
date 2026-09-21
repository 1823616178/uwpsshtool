using System;
using SshTool.App.Infrastructure;
using SshTool.Core.Sync;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Views.Sync
{
    // U16：登录/建库/解锁后的统一去向（02-UI-DESIGN.md §5.13 状态 Pivot 的过渡路由）。
    //   vault missing（账号从未建库）→ VaultSetupPage；
    //   vault locked（已建库未解锁）  → VaultUnlockPage；
    //   其余（ready / 状态不可读）    → 状态占位页（U17 建 AccountSyncPage 后替换该分支）。
    // U15 LoginPage 原来的 GoStatusPlaceholder 固定跳占位页，且完成后中间页留在返回栈里，
    // 按返回键会弹回已失效的登录页形成循环；本助手在导航后从返回栈剪掉失效中间页
    //（剪到哪由页面类型判定，见下），保证返回键直达上一个仍有效的页面。
    internal static class SyncNavigation
    {
        // 去向目标页。prune 形参保留以兼容既有调用点（LoginPage/VaultSetupPage/VaultUnlockPage
        // 传的是各自流程的「预期条数」），实际剪栈已改由类型判定，不再受这个数限制：
        // 固定条数会越剪——建库完成时登录页早已剪掉，按 prune=2 剪会把主页一并剪没；
        // 也会漏剪——解锁页走「已在栈里有登录页」的分支时 prune=1 只剪掉解锁页本身。
        public static void GoAfterAuth(Frame frame, int prune)
        {
            if (frame == null)
            {
                return;
            }
            Type target;
            object parameter = null;
            switch (ReadVaultStatus())
            {
                case VaultStatus.Missing:
                    target = typeof(VaultSetupPage);
                    break;
                case VaultStatus.Locked:
                    target = typeof(VaultUnlockPage);
                    break;
                default:
                    // ready → 状态页（U17 AccountSyncPage）；状态不可读（同步栈缺失/异常）
                    // → 仍走状态页（其 VM 会按 NeedsRouting 自行转出），避免被按进建库/解锁页
                    // 再被「请先登录/组件不可用」顶出来。
                    target = typeof(AccountSyncPage);
                    break;
            }
            frame.Navigate(target, parameter);
            // Navigate 只把「上一页」压进返回栈（当前页不入栈），所以栈顶就是刚离开的
            // 登录/建库/解锁页。逐个剪掉这类失效中间页，遇到任何其它类型（主页、终端页…）
            // 立即停止：登录→建库→完成这条路径剪完后栈顶仍是主页，主页留在栈内。
            while (frame.BackStack.Count > 0 && IsStaleAuthPage(
                frame.BackStack[frame.BackStack.Count - 1].SourcePageType))
            {
                frame.BackStack.RemoveAt(frame.BackStack.Count - 1);
            }
        }

        // 认证流程中间页：完成一次跳转后就不能再用（回去必被顶出来，形成返回循环）。
        private static bool IsStaleAuthPage(Type pageType)
        {
            return pageType == typeof(LoginPage)
                || pageType == typeof(VaultSetupPage)
                || pageType == typeof(VaultUnlockPage);
        }

        // 保险库状态读自 Coordinator 快照；读不到时返回 null（走占位页分支）。
        private static VaultStatus? ReadVaultStatus()
        {
            try
            {
                AppServices services = AppServices.Current;
                if (services != null && services.Sync != null)
                {
                    return services.Sync.State.Vault;
                }
            }
            catch (Exception)
            {
            }
            return null;
        }
    }
}
