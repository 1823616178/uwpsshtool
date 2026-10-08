using System;
using System.Globalization;
using SshTool.Core.Sync;
using Windows.ApplicationModel.Resources;

namespace SshTool.App.ViewModels.Sync
{
    // fix/functional-pass（P2-2）：Core 只给码（RelativeTimeKind / SyncMessageCode / SyncErrorCode），
    // 文案在这里查 resw（Sync_Relative_* / Sync_Msg_* / Sync_Err_*），英文界面不再出现中文。
    internal static class SyncText
    {
        public static string Relative(RelativeTime time, ResourceLoader loader)
        {
            switch (time.Kind)
            {
                case RelativeTimeKind.JustNow:
                    return Get(loader, "Sync_Relative_JustNow", "Just now");
                case RelativeTimeKind.Minutes:
                    return Format(Get(loader, "Sync_Relative_Minutes", "{0} min ago"), time.Value);
                case RelativeTimeKind.Hours:
                    return Format(Get(loader, "Sync_Relative_Hours", "{0} h ago"), time.Value);
                case RelativeTimeKind.Yesterday:
                    return Get(loader, "Sync_Relative_Yesterday", "Yesterday");
                case RelativeTimeKind.DayBeforeYesterday:
                    return Get(loader, "Sync_Relative_DayBeforeYesterday", "2 days ago");
                case RelativeTimeKind.Days:
                    return Format(Get(loader, "Sync_Relative_Days", "{0} days ago"), time.Value);
                case RelativeTimeKind.Date:
                    return time.Local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                default:
                    return string.Empty;
            }
        }

        // 状态卡消息：码 → resw；Error → 按异常类型本地化（ApiError / SyncOperationException），
        // 其他异常（如 App 侧本地适配器抛出的）回退 Core 诊断 Message。
        public static string StateMessage(SyncState state, ResourceLoader loader)
        {
            if (state == null)
            {
                return string.Empty;
            }
            switch (state.MessageCode)
            {
                case SyncMessageCode.None:
                    return state.Message ?? string.Empty;
                case SyncMessageCode.Error:
                    // fix/cold-start-login：App 侧本地适配器等抛出的普通异常不再把 Core / 平台的
                    // 原始诊断文本（常为中文）直接显示出来，改为本地化的「本机数据处理失败」+ 异常类型名。
                    SyncApplyException apply = state.MessageError as SyncApplyException;
                    if (apply != null)
                    {
                        return FormatName(Get(loader, "Sync_Err_Apply",
                            "Cloud data could not be applied on this device ({0})"), apply.Failure.ToString());
                    }
                    if (state.MessageError != null
                        && !(state.MessageError is SyncOperationException)
                        && !(state.MessageError is SshTool.Core.Sync.Api.ApiError))
                    {
                        return FormatName(Get(loader, "Sync_Err_Local",
                            "Sync failed while reading or writing local data ({0})"), state.MessageError.GetType().Name);
                    }
                    if (state.MessageError != null)
                    {
                        string described = VaultErrorText.Describe(state.MessageError, loader);
                        if (!string.IsNullOrEmpty(described))
                        {
                            return described;
                        }
                    }
                    return string.IsNullOrEmpty(state.Message)
                        ? Get(loader, "Sync_Error", "Sync failed")
                        : state.Message;
                default:
                    return Get(loader, "Sync_Msg_" + state.MessageCode.ToString(), state.Message ?? string.Empty);
            }
        }

        private static string FormatName(string template, string name)
        {
            try
            {
                return string.Format(CultureInfo.CurrentCulture, template, name);
            }
            catch (FormatException)
            {
                return template;
            }
        }

        public static string Error(SyncOperationException ex, ResourceLoader loader)
        {
            if (ex == null)
            {
                return string.Empty;
            }
            return Get(loader, "Sync_Err_" + ex.Code.ToString(), ex.Message ?? string.Empty);
        }

        private static string Format(string template, int value)
        {
            try
            {
                return string.Format(CultureInfo.CurrentCulture, template, value);
            }
            catch (FormatException)
            {
                return value.ToString(CultureInfo.CurrentCulture);
            }
        }

        private static string Get(ResourceLoader loader, string key, string fallback)
        {
            try
            {
                string value = loader == null ? null : loader.GetString(key);
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
            catch (Exception)
            {
            }
            return fallback;
        }
    }
}
