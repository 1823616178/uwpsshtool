namespace SshTool.Core.Sync.Auth
{
    // 03-SYNC-PROTOCOL.md §6.1 AuthState 会话视图（对应桌面端 AuthSession）。
    // 只含用户与设备身份，不含任何 token；token 由 AuthTokens / AuthStore.Tokens 承载。
    public sealed class AuthState
    {
        public bool Authenticated { get; set; }

        public string UserId { get; set; }

        public string UserEmail { get; set; }

        public string DeviceId { get; set; }

        public string DeviceName { get; set; }

        public static AuthState Unauthenticated
        {
            get { return new AuthState { Authenticated = false }; }
        }

        public AuthState Clone()
        {
            return new AuthState
            {
                Authenticated = Authenticated,
                UserId = UserId,
                UserEmail = UserEmail,
                DeviceId = DeviceId,
                DeviceName = DeviceName
            };
        }
    }

    // 桌面端 AuthTokens：accessToken / refreshToken / expiresAt（Unix 毫秒）。
    public sealed class AuthTokens
    {
        public string AccessToken { get; set; }

        public string RefreshToken { get; set; }

        public long ExpiresAt { get; set; }

        public AuthTokens Clone()
        {
            return new AuthTokens
            {
                AccessToken = AccessToken,
                RefreshToken = RefreshToken,
                ExpiresAt = ExpiresAt
            };
        }
    }
}
