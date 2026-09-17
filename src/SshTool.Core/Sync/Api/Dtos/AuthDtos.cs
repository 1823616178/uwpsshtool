using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace SshTool.Core.Sync.Api.Dtos
{
    // 03-SYNC-PROTOCOL.md §2.2 认证/账号/设备分组的响应与请求体。
    // 解析规则：必需键缺失/类型不符 → ProtocolParseException；未知键忽略。

    public sealed class AuthUserDto
    {
        public string Id { get; set; }
        public string Email { get; set; }

        public static AuthUserDto Parse(JObject o, string path)
        {
            return new AuthUserDto
            {
                Id = DtoReader.Str(o, "id", path + ".id"),
                Email = DtoReader.Str(o, "email", path + ".email")
            };
        }
    }

    public sealed class AuthDeviceDto
    {
        public string Id { get; set; }
        public string Name { get; set; }

        public static AuthDeviceDto Parse(JObject o, string path)
        {
            return new AuthDeviceDto
            {
                Id = DtoReader.Str(o, "id", path + ".id"),
                Name = DtoReader.Str(o, "name", path + ".name")
            };
        }
    }

    // POST auth/register / auth/login / auth/refresh 的响应
    public sealed class AuthTokenResponse
    {
        public string AccessToken { get; set; }
        public string RefreshToken { get; set; }
        public int ExpiresIn { get; set; }
        public AuthUserDto User { get; set; }
        public AuthDeviceDto Device { get; set; }

        public static AuthTokenResponse Parse(JObject o)
        {
            return new AuthTokenResponse
            {
                AccessToken = DtoReader.Str(o, "accessToken", "accessToken"),
                RefreshToken = DtoReader.Str(o, "refreshToken", "refreshToken"),
                ExpiresIn = DtoReader.Int(o, "expiresIn", "expiresIn"),
                User = AuthUserDto.Parse(DtoReader.Obj(o, "user", "user"), "user"),
                Device = AuthDeviceDto.Parse(DtoReader.Obj(o, "device", "device"), "device")
            };
        }
    }

    // register/login 请求体里的 device 字段
    public sealed class DeviceDescriptorData
    {
        public string Name { get; set; }
        public string Platform { get; set; }
        public string AppVersion { get; set; }

        public JObject ToJson()
        {
            return new JObject
            {
                ["name"] = Name,
                ["platform"] = Platform,
                ["appVersion"] = AppVersion
            };
        }
    }

    // GET me 响应
    public sealed class MeResponse
    {
        public AuthUserDto User { get; set; }
        public string DeviceId { get; set; }

        public static MeResponse Parse(JObject o)
        {
            return new MeResponse
            {
                User = AuthUserDto.Parse(DtoReader.Obj(o, "user", "user"), "user"),
                DeviceId = DtoReader.Str(o, "deviceId", "deviceId")
            };
        }
    }

    public sealed class DeviceInfoDto
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Platform { get; set; }
        public string AppVersion { get; set; }
        public string CreatedAt { get; set; }
        public string LastSeenAt { get; set; }
        public bool Current { get; set; }

        public static DeviceInfoDto Parse(JObject o, string path)
        {
            return new DeviceInfoDto
            {
                Id = DtoReader.Str(o, "id", path + ".id"),
                Name = DtoReader.Str(o, "name", path + ".name"),
                Platform = DtoReader.Str(o, "platform", path + ".platform"),
                AppVersion = DtoReader.Str(o, "appVersion", path + ".appVersion"),
                CreatedAt = DtoReader.Str(o, "createdAt", path + ".createdAt"),
                LastSeenAt = DtoReader.Str(o, "lastSeenAt", path + ".lastSeenAt"),
                Current = DtoReader.Bool(o, "current", path + ".current")
            };
        }
    }

    // GET devices 响应
    public sealed class DeviceListResponse
    {
        public IReadOnlyList<DeviceInfoDto> Items { get; set; }

        public static DeviceListResponse Parse(JObject o)
        {
            var array = DtoReader.Arr(o, "items", "items");
            var items = new List<DeviceInfoDto>(array.Count);
            for (int i = 0; i < array.Count; i++)
            {
                items.Add(DeviceInfoDto.Parse(DtoReader.ItemObj(array[i], "items[" + i + "]"), "items[" + i + "]"));
            }
            return new DeviceListResponse { Items = items };
        }
    }

    // POST auth/logout-all 响应
    public sealed class LogoutAllResponse
    {
        public int RevokedDevices { get; set; }
        public int RevokedRefreshTokens { get; set; }

        public static LogoutAllResponse Parse(JObject o)
        {
            return new LogoutAllResponse
            {
                RevokedDevices = DtoReader.Int(o, "revokedDevices", "revokedDevices"),
                RevokedRefreshTokens = DtoReader.Int(o, "revokedRefreshTokens", "revokedRefreshTokens")
            };
        }
    }

    // DELETE me 响应
    public sealed class DeleteAccountResponse
    {
        public bool Deleted { get; set; }
        public bool SessionsInvalidated { get; set; }

        public static DeleteAccountResponse Parse(JObject o)
        {
            return new DeleteAccountResponse
            {
                Deleted = DtoReader.Bool(o, "deleted", "deleted"),
                SessionsInvalidated = DtoReader.Bool(o, "sessionsInvalidated", "sessionsInvalidated")
            };
        }
    }
}
