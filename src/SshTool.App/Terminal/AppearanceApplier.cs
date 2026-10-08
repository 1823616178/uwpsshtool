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
                // fix/functional-pass（P1-4）：内置外观的字号跟随设置项 terminalFontSize，
                // 自定义外观用自身字号（TerminalFontSizePolicy）。
                AppearanceProfile profile = await TerminalFontSizePolicy.ResolveAsync(
                    services.AppearanceService, services.Settings, host).ConfigureAwait(false);
                return profile ?? Defaults.DefaultAppearance();
            }
            catch (Exception)
            {
                return Defaults.DefaultAppearance();
            }
        }

        // fix/functional-pass（P1-4）：双指缩放 / 快捷键改字号后的持久化（UI 线程调用）：
        // 内置外观 → 设置项 terminalFontSize；自定义外观 → 更新该外观（经 Changed 刷新其他终端）。
        public static async Task PersistFontSizeAsync(string hostId, int size)
        {
            AppServices services = AppServices.Current;
            if (services == null || services.AppearanceService == null || services.Settings == null)
            {
                return;
            }
            Host host = string.IsNullOrEmpty(hostId)
                ? null
                : await services.Hosts.GetByIdAsync(hostId).ConfigureAwait(true);
            TerminalFontSizePolicy.PersistPlan plan = await TerminalFontSizePolicy
                .PlanPersistAsync(services.AppearanceService, host, size).ConfigureAwait(true);
            if (plan.NoChange)
            {
                return;
            }
            if (plan.AppearanceToUpdate != null)
            {
                await services.AppearanceService.UpdateAsync(plan.AppearanceToUpdate).ConfigureAwait(true);
                return;
            }
            services.Settings.TerminalFontSize = plan.Size;
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
