using SshTool.Core.Models;

namespace SshTool.Core.Validation
{
    // 01-DESIGN.md §8.1 / 03-SYNC-PROTOCOL.md §4.1 的 Host 约束
    public static class HostValidator
    {
        public static ValidationResult Validate(Host host)
        {
            var result = new ValidationResult();
            if (host == null)
            {
                result.Add("host", ValidationKeys.Required);
                return result;
            }

            if (string.IsNullOrWhiteSpace(host.Name))
            {
                result.Add("name", ValidationKeys.Required);
            }
            else if (host.Name.Length > 255)
            {
                result.Add("name", ValidationKeys.NameTooLong);
            }

            if (string.IsNullOrWhiteSpace(host.HostName))
            {
                result.Add("host", ValidationKeys.Required);
            }
            else if (host.HostName.Length > 1024)
            {
                result.Add("host", ValidationKeys.HostTooLong);
            }

            if (string.IsNullOrWhiteSpace(host.Username))
            {
                result.Add("username", ValidationKeys.Required);
            }
            else if (host.Username.Length > 255)
            {
                result.Add("username", ValidationKeys.NameTooLong);
            }

            if (host.Port < 1 || host.Port > 65535)
            {
                result.Add("port", ValidationKeys.PortRange);
            }
            if (host.Keepalive < 0 || host.Keepalive > 3600)
            {
                result.Add("keepalive", ValidationKeys.KeepaliveRange);
            }
            return result;
        }
    }
}
