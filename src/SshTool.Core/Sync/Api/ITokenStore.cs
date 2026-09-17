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

        // S07 刷新策略：refreshUncertain==true 时返回 false
        bool CanRefresh { get; }
        void MarkRefreshUncertain();
    }
}
