using SshTool.Core.Sync;
using SshTool.Core.Sync.Auth;
using SshTool.Core.Sync.Vault;
using Xunit;

namespace SshTool.Core.Tests.Sync
{
    // fix/login-feedback：账号与同步页的统一去向。回归点：未登录必须去登录页
    //（此前 App 只按 Vault 路由，未登录时 Vault 恒为 Missing → 被送进建库页，登录页无从到达）。
    public class SyncRoutingTests
    {
        private static AuthState SignedIn()
        {
            return new AuthState { Authenticated = true, UserId = "u1", DeviceId = "d1" };
        }

        private static SyncState Vault(VaultStatus vault)
        {
            return new SyncState { Vault = vault, Phase = SyncPhase.Idle };
        }

        [Theory]
        [InlineData(VaultStatus.Missing)]
        [InlineData(VaultStatus.Locked)]
        [InlineData(VaultStatus.Ready)]
        public void Unauthenticated_AlwaysRoutesToLogin_RegardlessOfVault(VaultStatus vault)
        {
            Assert.Equal(SyncScreenKind.Login, SyncRouting.Decide(true, AuthState.Unauthenticated, Vault(vault)));
            Assert.Equal(SyncScreenKind.Login, SyncRouting.Decide(true, null, Vault(vault)));
        }

        // 未登录时 SyncCoordinator.BuildState 给出的正是 SignedOut + Missing：旧路由在这里去了建库页。
        [Fact]
        public void SignedOutState_RoutesToLogin_NotVaultSetup()
        {
            var signedOut = new SyncState { Vault = VaultStatus.Missing, Phase = SyncPhase.SignedOut };
            Assert.Equal(SyncScreenKind.Login, SyncRouting.Decide(true, AuthState.Unauthenticated, signedOut));
        }

        [Fact]
        public void Authenticated_RoutesByVault()
        {
            Assert.Equal(SyncScreenKind.CreateVault, SyncRouting.Decide(true, SignedIn(), Vault(VaultStatus.Missing)));
            Assert.Equal(SyncScreenKind.UnlockVault, SyncRouting.Decide(true, SignedIn(), Vault(VaultStatus.Locked)));
            Assert.Equal(SyncScreenKind.Status, SyncRouting.Decide(true, SignedIn(), Vault(VaultStatus.Ready)));
        }

        [Fact]
        public void Authenticated_NullState_TreatedAsMissingVault()
        {
            Assert.Equal(SyncScreenKind.CreateVault, SyncRouting.Decide(true, SignedIn(), null));
        }

        // 同步栈缺失：去登录页（提交时提示「同步组件不可用」），不再转回状态页自己。
        [Fact]
        public void SyncUnavailable_RoutesToLogin_EvenIfSignedIn()
        {
            Assert.Equal(SyncScreenKind.Login, SyncRouting.Decide(false, SignedIn(), Vault(VaultStatus.Ready)));
            Assert.Equal(SyncScreenKind.Login, SyncRouting.Decide(false, null, null));
        }

        [Fact]
        public void ShouldNavigate_SamePage_IsFalse()
        {
            Assert.False(SyncRouting.ShouldNavigate(SyncScreenKind.Login, SyncScreenKind.Login));
            Assert.False(SyncRouting.ShouldNavigate(SyncScreenKind.Status, SyncScreenKind.Status));
            Assert.False(SyncRouting.ShouldNavigate(SyncScreenKind.CreateVault, SyncScreenKind.CreateVault));
        }

        [Fact]
        public void ShouldNavigate_DifferentOrForeignPage_IsTrue()
        {
            Assert.True(SyncRouting.ShouldNavigate(SyncScreenKind.Status, SyncScreenKind.Login));
            Assert.True(SyncRouting.ShouldNavigate(SyncScreenKind.Login, SyncScreenKind.CreateVault));
            Assert.True(SyncRouting.ShouldNavigate(null, SyncScreenKind.Login));
            Assert.True(SyncRouting.ShouldNavigate(null, SyncScreenKind.Status));
        }

        // 登录页上已登录时的重定向不会弹回登录页（任何已登录状态都不会被判成 Login）。
        [Theory]
        [InlineData(VaultStatus.Missing)]
        [InlineData(VaultStatus.Locked)]
        [InlineData(VaultStatus.Ready)]
        public void SignedInOnLoginPage_NeverBouncesBackToLogin(VaultStatus vault)
        {
            SyncScreenKind target = SyncRouting.Decide(true, SignedIn(), Vault(vault));
            Assert.NotEqual(SyncScreenKind.Login, target);
            Assert.True(SyncRouting.ShouldNavigate(SyncScreenKind.Login, target));
        }
    }
}
