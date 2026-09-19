using System;
using System.Threading;
using System.Threading.Tasks;
using SshTool.Core.Common;
using SshTool.Core.Sync.Api;
using SshTool.Core.Sync.Api.Dtos;

namespace SshTool.Core.Sync.Auth
{
    // 03-SYNC-PROTOCOL.md §7.1 账号流程（对应桌面端 sync-coordinator.ts 的
    // register / login / logout / changeAccountPassword / deleteAccount /
    // listDevices / renameDevice / revokeDevice）。
    // 日志脱敏：只记操作相位，绝不记录密码、token、邀请码与请求体。
    public sealed class AuthService
    {
        private readonly AuthStore _store;
        private readonly ApiClient _api;
        private readonly IDeviceDescriptorProvider _devices;
        private readonly ILogger _logger;

        public AuthService(
            AuthStore store,
            ApiClient api,
            IDeviceDescriptorProvider devices,
            ILogger logger = null)
        {
            if (store == null)
            {
                throw new ArgumentNullException(nameof(store));
            }
            if (api == null)
            {
                throw new ArgumentNullException(nameof(api));
            }
            if (devices == null)
            {
                throw new ArgumentNullException(nameof(devices));
            }
            _store = store;
            _api = api;
            _devices = devices;
            _logger = logger;
        }

        public AuthState Session
        {
            get { return _store.Session; }
        }

        // 注册。已登录时拒绝（每次登录都会新建设备，见 §11 踩坑 10）。
        public async Task<AuthState> RegisterAsync(
            string email,
            string password,
            string inviteCode = null,
            string deviceName = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            RequireCredential(email, nameof(email));
            RequireCredential(password, nameof(password));
            EnsureNotSignedIn();
            var descriptor = _devices.GetDescriptor(deviceName);
            var result = await _api.RegisterAsync(email, password, inviteCode, descriptor, cancellationToken)
                .ConfigureAwait(false);
            await _store.SaveAsync(result).ConfigureAwait(false);
            Info("注册成功");
            return _store.Session;
        }

        // 登录。已登录时拒绝（同上）。
        public async Task<AuthState> LoginAsync(
            string email,
            string password,
            string deviceName = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            RequireCredential(email, nameof(email));
            RequireCredential(password, nameof(password));
            EnsureNotSignedIn();
            var descriptor = _devices.GetDescriptor(deviceName);
            var result = await _api.LoginAsync(email, password, descriptor, cancellationToken)
                .ConfigureAwait(false);
            await _store.SaveAsync(result).ConfigureAwait(false);
            Info("登录成功");
            return _store.Session;
        }

        // 登出。服务端失败也清本地（finally），失败原样上抛。
        public async Task LogoutAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!_store.Session.Authenticated)
            {
                await _store.ClearAsync().ConfigureAwait(false);
                return;
            }
            try
            {
                await _api.LogoutAsync(cancellationToken).ConfigureAwait(false);
                Info("已退出登录");
            }
            finally
            {
                await _store.ClearAsync().ConfigureAwait(false);
            }
        }

        // 全部登出（撤销所有设备）。语义同 LogoutAsync。
        public async Task LogoutAllAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (!_store.Session.Authenticated)
            {
                await _store.ClearAsync().ConfigureAwait(false);
                return;
            }
            try
            {
                await _api.LogoutAllAsync(cancellationToken).ConfigureAwait(false);
                Info("已退出所有设备");
            }
            finally
            {
                await _store.ClearAsync().ConfigureAwait(false);
            }
        }

        // 改登录密码。成功后清本地（需用新密码重新登录）；失败不清。
        public async Task ChangeLoginPasswordAsync(
            string currentPassword,
            string newPassword,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            RequireCredential(currentPassword, nameof(currentPassword));
            RequireCredential(newPassword, nameof(newPassword));
            await _api.ChangePasswordAsync(currentPassword, newPassword, cancellationToken)
                .ConfigureAwait(false);
            await _store.ClearAsync().ConfigureAwait(false);
            Info("登录密码已修改，需重新登录");
        }

        // 注销账号。成功后清本地；失败不清。
        public async Task DeleteAccountAsync(
            string currentPassword,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            RequireCredential(currentPassword, nameof(currentPassword));
            await _api.DeleteAccountAsync(currentPassword, cancellationToken).ConfigureAwait(false);
            await _store.ClearAsync().ConfigureAwait(false);
            Info("账号已注销");
        }

        public Task<DeviceListResponse> ListDevicesAsync(
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return _api.ListDevicesAsync(cancellationToken);
        }

        public async Task RenameDeviceAsync(
            string deviceId,
            string name,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                throw new ArgumentException("设备 id 不能为空", nameof(deviceId));
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("设备名称不能为空", nameof(name));
            }
            await _api.RenameDeviceAsync(deviceId, name, cancellationToken).ConfigureAwait(false);
            Info("设备已改名");
        }

        // 拒绝撤销本机（与桌面端 revokeDevice 一致：本机请用退出登录）。
        public async Task RevokeDeviceAsync(
            string deviceId,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                throw new ArgumentException("设备 id 不能为空", nameof(deviceId));
            }
            var session = _store.Session;
            if (session.Authenticated
                && string.Equals(session.DeviceId, deviceId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("不能撤销当前设备，请使用退出登录");
            }
            await _api.DeleteDeviceAsync(deviceId, cancellationToken).ConfigureAwait(false);
            Info("设备已撤销");
        }

        private void EnsureNotSignedIn()
        {
            if (_store.Session.Authenticated)
            {
                throw new InvalidOperationException("已登录，请先退出登录后再继续");
            }
        }

        private static void RequireCredential(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(name + "不能为空", name);
            }
        }

        private void Info(string message)
        {
            if (_logger != null)
            {
                _logger.Log(LogLevel.Info, "Auth", message);
            }
        }
    }
}
