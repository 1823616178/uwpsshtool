using System.Globalization;
using SshTool.Core.Common;
using SshTool.Core.Forwarding;

namespace SshTool.App.Infrastructure
{
    // fix/functional-pass：隧道相关的界面文案。Core（TunnelManager / TunnelConnector）只给
    // 类别码 + 运行时原文，这里统一本地化（P2-1：此前隧道错误是写死的中文/英文兜底或裸错误码）。
    public static class TunnelText
    {
        public static string ErrorCodeText(SshErrorCode code)
        {
            string number = ((int)code).ToString(CultureInfo.InvariantCulture);
            return Localized.Get("Error_" + number,
                Localized.Format("Tunnel_ErrorCodeFallback", "错误码 {0}", number));
        }

        public static string ConnectFailure(TunnelConnectResult result)
        {
            if (result == null)
            {
                return Localized.Get("Tunnel_StartFailed", "启动失败");
            }
            string text;
            switch (result.Failure)
            {
                case TunnelConnectFailure.JumpPlanInvalid:
                    text = Localized.Get("Tunnel_JumpPlanInvalid", "跳板链配置无效（存在循环或超过 5 跳）");
                    break;
                case TunnelConnectFailure.UnknownHostKey:
                    text = Localized.Get("Tunnel_UnknownHostKey", "未知主机密钥：请先在终端里连接该主机并确认信任，再启动隧道");
                    break;
                case TunnelConnectFailure.HostKeyMismatch:
                    text = Localized.Get("Tunnel_HostKeyMismatch", "主机密钥与已信任的记录不一致：请先在终端里连接该主机核实");
                    break;
                case TunnelConnectFailure.NoSavedCredential:
                    text = Localized.Get("Tunnel_NoSavedCredential", "没有已保存的凭据：隧道无法弹框询问，请先保存密码/短语");
                    break;
                case TunnelConnectFailure.AuthFailed:
                    text = Localized.Format("Tunnel_AuthFailedDetail", "认证失败：{0}", ErrorCodeText(result.Code));
                    break;
                default:
                    text = Localized.Format("Tunnel_ConnectFailedDetail", "连接服务器失败：{0}", ErrorCodeText(result.Code));
                    break;
            }
            if (!string.IsNullOrEmpty(result.FailedHop))
            {
                text = Localized.Format("Tunnel_HopPrefix", "跳板 {0}：{1}", result.FailedHop, text);
            }
            return text;
        }

        public static string Status(TunnelMessageCode code, string detail, int reconnectDelaySeconds)
        {
            switch (code)
            {
                case TunnelMessageCode.Detail:
                    return detail ?? string.Empty;
                case TunnelMessageCode.Connecting:
                    return Localized.Get("TunnelState_Connecting", "连接中");
                case TunnelMessageCode.Established:
                    return string.IsNullOrEmpty(detail)
                        ? Localized.Get("Tunnel_Established", "隧道已建立")
                        : detail;
                case TunnelMessageCode.StartFailed:
                    return Localized.Get("Tunnel_StartFailed", "启动失败");
                case TunnelMessageCode.StoppedManually:
                    return Localized.Get("Tunnel_StoppedManually", "已手动停止");
                case TunnelMessageCode.ConfigDeleted:
                    return Localized.Get("Tunnel_ConfigDeleted", "配置已删除");
                case TunnelMessageCode.ManagerDisposed:
                    return Localized.Get("Tunnel_ManagerDisposed", "隧道服务已释放");
                case TunnelMessageCode.LinkLost:
                    return Localized.Get("Forwarder_LinkLost", "会话链路中断");
                case TunnelMessageCode.RelayDisabled:
                    return Localized.Get("Forwarder_RelayDesktopOnly", "中转隧道仅在桌面端运行");
                case TunnelMessageCode.ReconnectScheduled:
                {
                    string reason = string.IsNullOrEmpty(detail)
                        ? Localized.Get("Forwarder_LinkLost", "会话链路中断")
                        : detail;
                    return Localized.Format("Tunnel_ReconnectIn", "{0}，{1} 秒后重连", reason, reconnectDelaySeconds);
                }
                default:
                    return string.Empty;
            }
        }
    }
}
