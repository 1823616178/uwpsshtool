using SshTool.Core.Sync.Api;
using SshTool.Core.Sync.Api.Dtos;

namespace SshTool.Core.Tests.Fakes
{
    // ITokenStore 内存实现：记录 Save/Clear/MarkRefreshUncertain 调用供断言。
    public sealed class FakeTokenStore : ITokenStore
    {
        public TokenPair Tokens;
        public AuthTokenResponse Saved;
        public bool Cleared;
        public bool Uncertain;

        public static FakeTokenStore SignedIn()
        {
            return new FakeTokenStore { Tokens = new TokenPair("access-1", "refresh-1") };
        }

        public TokenPair GetTokens()
        {
            return Tokens;
        }

        public void Save(AuthTokenResponse tokens)
        {
            Saved = tokens;
            Uncertain = false; // §2.4.5：保存新 token 后 uncertain 复位
            Tokens = new TokenPair(tokens.AccessToken, tokens.RefreshToken);
        }

        public void Clear()
        {
            Tokens = null;
            Cleared = true;
        }

        public bool CanRefresh
        {
            get { return !Uncertain; }
        }

        public void MarkRefreshUncertain()
        {
            Uncertain = true;
        }
    }
}
