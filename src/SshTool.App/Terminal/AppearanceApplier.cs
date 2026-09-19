using System;
using System.Threading.Tasks;
using SshTool.App.Infrastructure;
using SshTool.Core.Appearance;
using SshTool.Core.Models;
using SshTool.Core.Storage.Repositories;

namespace SshTool.App.Terminal
{
    // A03：外观 → TerminalView 的应用入口（已打开终端收到外观变化后刷新
    // 调色板与字体度量，不重连）。解析链复用 AppearanceService；绘制细节
    // 在 TerminalView.ApplyAppearance（UI 线程）。
    public static class AppearanceApplier
    {
        public static async Task ApplyForHostAsync(TerminalView view, string hostId)
        {
            if (view == null)
            {
                return;
            }
            AppearanceProfile profile = await ResolveForHostAsync(hostId).ConfigureAwait(false);
            if (profile == null)
            {
                return;
            }
            DispatcherHelper.Post(() =>
            {
                try
                {
                    view.ApplyAppearance(profile);
                }
                catch (Exception)
                {
                }
            });
        }

        public static async Task<AppearanceProfile> ResolveForHostAsync(string hostId)
        {
            try
            {
                AppServices services = AppServices.Current;
                if (services == null || services.AppearanceService == null)
                {
                    return Defaults.DefaultAppearance();
                }
                Host host = null;
                if (!string.IsNullOrEmpty(hostId))
                {
                    host = await services.Hosts.GetByIdAsync(hostId).ConfigureAwait(false);
                }
                ResolvedAppearance resolved = await services.AppearanceService.ResolveAsync(host).ConfigureAwait(false);
                if (resolved == null || resolved.Profile == null)
                {
                    return Defaults.DefaultAppearance();
                }
                return resolved.Profile;
            }
            catch (Exception)
            {
                return Defaults.DefaultAppearance();
            }
        }

        public static bool NeedsRefresh(AppearanceChangedEventArgs change, string hostId)
        {
            if (change == null)
            {
                return true;
            }
            if (change.AffectedHostIds != null)
            {
                for (int i = 0; i < change.AffectedHostIds.Count; i++)
                {
                    if (string.Equals(change.AffectedHostIds[i], hostId, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            // 临时会话（hostId 为空）永远跟随全局默认：默认外观被改或换了
            // 默认（Updated/DefaultChanged）时刷新；增删不改变解析结果。
            if (string.IsNullOrEmpty(hostId))
            {
                return change.Kind == AppearanceChangeKind.Updated
                    || change.Kind == AppearanceChangeKind.DefaultChanged;
            }
            return false;
        }
    }
}
