using System.Threading.Tasks;
using SshTool.Core.Models;
using SshTool.Core.Storage;

namespace SshTool.Core.Appearance
{
    // fix/functional-pass（P1-4）：终端字号的来源与持久化。
    //   - 内置外观（只读，所有设备共享）→ 字号取设置项 terminalFontSize（设置页滑块终于生效）；
    //   - 用户自定义外观且 FontSize > 0 → 取外观自身的字号；
    //   - 双指缩放 / 快捷键改字号后持久化到同一个来源：内置 → 设置项；自定义 → 更新该外观。
    public static class TerminalFontSizePolicy
    {
        public const string SettingKey = "terminalFontSize";

        public static int DefaultSize
        {
            get { return (int)SettingDefinitions.Require(SettingKey).DefaultValue; }
        }

        public static int Clamp(int size)
        {
            return SettingDefinitions.Require(SettingKey).Clamp(size);
        }

        public static int Effective(AppearanceProfile profile, bool isBuiltIn, int settingSize)
        {
            if (!isBuiltIn && profile != null && profile.FontSize > 0)
            {
                return Clamp(profile.FontSize);
            }
            return Clamp(settingSize > 0 ? settingSize : DefaultSize);
        }

        // 返回带有效字号的副本（不修改传入实例：内置外观是共享单例）。
        public static AppearanceProfile WithEffectiveFontSize(AppearanceProfile profile, bool isBuiltIn, int settingSize)
        {
            if (profile == null)
            {
                return null;
            }
            AppearanceProfile copy = profile.Clone();
            copy.FontSize = Effective(profile, isBuiltIn, settingSize);
            return copy;
        }

        public static async Task<AppearanceProfile> ResolveAsync(
            AppearanceService appearances, SettingsRepository settings, Host host)
        {
            ResolvedAppearance resolved = await appearances.ResolveAsync(host).ConfigureAwait(false);
            if (resolved == null || resolved.Profile == null)
            {
                return null;
            }
            bool builtIn = appearances.IsBuiltIn(resolved.Profile.Id);
            return WithEffectiveFontSize(resolved.Profile, builtIn, settings.TerminalFontSize);
        }

        // 字号变化后的持久化计划：AppearanceToUpdate 非空 → 更新该自定义外观；否则写设置项
        // （由调用方在 UI 线程执行写入，设置变更事件的订阅者多在 UI 线程）。
        public sealed class PersistPlan
        {
            public int Size { get; set; }
            public AppearanceProfile AppearanceToUpdate { get; set; }
            public bool NoChange { get; set; }
        }

        public static async Task<PersistPlan> PlanPersistAsync(AppearanceService appearances, Host host, int size)
        {
            int clamped = Clamp(size);
            ResolvedAppearance resolved = await appearances.ResolveAsync(host).ConfigureAwait(false);
            if (resolved == null || resolved.Profile == null || appearances.IsBuiltIn(resolved.Profile.Id))
            {
                return new PersistPlan { Size = clamped };
            }
            if (resolved.Profile.FontSize == clamped)
            {
                return new PersistPlan { Size = clamped, NoChange = true };
            }
            AppearanceProfile copy = resolved.Profile.Clone();
            copy.FontSize = clamped;
            return new PersistPlan { Size = clamped, AppearanceToUpdate = copy };
        }
    }
}
