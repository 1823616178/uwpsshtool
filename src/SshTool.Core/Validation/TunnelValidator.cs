using System;
using SshTool.Core.Models;

namespace SshTool.Core.Validation
{
    // Tunnel：name 1–255；serverId 必填；listenPort 1–65535；
    // 按类型必填：dynamic 不需要 dest；local/remote 需要 destHost + destPort 1–65535；
    // relay 需要 destServerId（≤255，传 hostExists 时须指向存在的主机）。
    public static class TunnelValidator
    {
        public static ValidationResult Validate(Tunnel tunnel, Func<string, bool> hostExists = null)
        {
            var result = new ValidationResult();
            if (tunnel == null)
            {
                result.Add("tunnel", ValidationKeys.Required);
                return result;
            }

            if (string.IsNullOrWhiteSpace(tunnel.Name))
            {
                result.Add("name", ValidationKeys.Required);
            }
            else if (tunnel.Name.Length > 255)
            {
                result.Add("name", ValidationKeys.NameTooLong);
            }

            if (string.IsNullOrWhiteSpace(tunnel.ServerId))
            {
                result.Add("serverId", ValidationKeys.ServerRequired);
            }

            if (tunnel.ListenPort < 1 || tunnel.ListenPort > 65535)
            {
                result.Add("listenPort", ValidationKeys.PortRange);
            }
            if (tunnel.ListenHost != null && tunnel.ListenHost.Length > 1024)
            {
                result.Add("listenHost", ValidationKeys.HostTooLong);
            }

            switch (tunnel.Type)
            {
                case TunnelType.Local:
                case TunnelType.Remote:
                    if (string.IsNullOrWhiteSpace(tunnel.DestHost))
                    {
                        result.Add("destHost", ValidationKeys.DestHostRequired);
                    }
                    if (tunnel.DestPort < 1 || tunnel.DestPort > 65535)
                    {
                        result.Add("destPort", ValidationKeys.PortRange);
                    }
                    break;
                case TunnelType.Relay:
                    if (string.IsNullOrWhiteSpace(tunnel.DestServerId))
                    {
                        result.Add("destServerId", ValidationKeys.DestServerRequired);
                    }
                    else
                    {
                        if (tunnel.DestServerId.Length > 255)
                        {
                            result.Add("destServerId", ValidationKeys.NameTooLong);
                        }
                        else if (hostExists != null && !hostExists(tunnel.DestServerId))
                        {
                            result.Add("destServerId", ValidationKeys.DestServerRequired);
                        }
                    }
                    break;
                case TunnelType.Dynamic:
                default:
                    break;
            }
            return result;
        }
    }
}
