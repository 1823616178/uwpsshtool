namespace SshTool.Core.Sync.Api
{
    public sealed class TokenPair
    {
        public TokenPair(string accessToken, string refreshToken)
        {
            AccessToken = accessToken;
            RefreshToken = refreshToken;
        }

        public string AccessToken { get; private set; }
        public string RefreshToken { get; private set; }
    }

    // 桌面端 ApiTokenStore 的移植；持久化实现是 S08 的 AuthStore（secure/auth.bin）。
    public interface ITokenStore
    {
        // null = 未登录
        TokenPair GetTokens();
        void Save(Dtos.AuthTokenResponse tokens);
        void Clear();

        // refreshUncertain==true 时返回 false。fix/persist-login 起只作诊断：ApiClient 不再据此
        // 拒绝刷新（结果不明的 refreshToken 仍试一次，由服务端裁决，见 ApiClient.PerformRefreshAsync）。
        bool CanRefresh { get; }
        void MarkRefreshUncertain();
    }
}
