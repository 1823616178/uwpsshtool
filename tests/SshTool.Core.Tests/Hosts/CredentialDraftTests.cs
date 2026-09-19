using System.Linq;
using SshTool.Core.Hosts;
using SshTool.Core.Models;
using SshTool.Core.Storage;
using Xunit;

namespace SshTool.Core.Tests.Hosts
{
    public class CredentialDraftTests
    {
        [Fact]
        public void New_RememberPasswordDefaultOn()
        {
            CredentialDraft draft = CredentialDraft.ForNew();
            Assert.Equal(AuthType.Password, draft.AuthType);
            Assert.True(draft.RememberPassword);
        }

        [Fact]
        public void SetPassword_Remember_WritesSecret()
        {
            CredentialDraft draft = CredentialDraft.ForNew();
            draft.SetPassword("s3cret");
            SecretMutation m = draft.BuildMutations("h1").Single();
            Assert.Equal(SecretKeys.HostPassword("h1"), m.Key);
            Assert.Equal("s3cret", m.Value);
        }

        [Fact]
        public void UncheckRemember_RemovesSaved()
        {
            CredentialDraft draft = CredentialDraft.ForNew();
            draft.HasSavedPassword = true;
            draft.SetRememberPassword(false);
            SecretMutation m = draft.BuildMutations("h1").First();
            Assert.Equal(SecretKeys.HostPassword("h1"), m.Key);
            Assert.Null(m.Value);
        }

        [Fact]
        public void RecheckRemember_WithoutTyping_KeepsSaved()
        {
            CredentialDraft draft = CredentialDraft.ForNew();
            draft.HasSavedPassword = true;
            draft.SetRememberPassword(false);
            draft.SetRememberPassword(true);
            Assert.Equal(CredentialFieldState.Unchanged, draft.PasswordState);
            Assert.Empty(draft.BuildMutations("h1").Where(x => x.Key == SecretKeys.HostPassword("h1")));
        }

        [Fact]
        public void UnchangedSavedPassword_NoMutation()
        {
            CredentialDraft draft = CredentialDraft.ForNew();
            draft.HasSavedPassword = true;
            Assert.Empty(draft.BuildMutations("h1").Where(x => x.Key == SecretKeys.HostPassword("h1")));
        }

        [Fact]
        public void SwitchPasswordToKey_NeedsConfirmAndClearsPassword()
        {
            CredentialDraft draft = CredentialDraft.ForNew();
            draft.HasSavedPassword = true;
            AuthSwitchPreview preview = draft.PreviewSwitch(AuthType.Key);
            Assert.True(preview.NeedsConfirm);
            draft.ApplySwitch(AuthType.Key);
            Assert.Equal(AuthType.Key, draft.AuthType);
            Assert.Equal(string.Empty, draft.Password);
            Assert.Equal(CredentialFieldState.Cleared, draft.PasswordState);
            Assert.Null(draft.BuildMutations("h1").Single(x => x.Key == SecretKeys.HostPassword("h1")).Value);
        }

        [Fact]
        public void SwitchKeyToPassword_ClearsKeyAndPassphrase()
        {
            CredentialDraft draft = CredentialDraft.ForNew();
            draft.AuthType = AuthType.Key;
            draft.KeyId = "k1";
            draft.HasSavedPassphrase = true;
            AuthSwitchPreview preview = draft.PreviewSwitch(AuthType.Password);
            Assert.True(preview.NeedsConfirm);
            draft.ApplySwitch(AuthType.Password);
            Assert.Null(draft.KeyId);
            Assert.Equal(CredentialFieldState.Cleared, draft.PassphraseState);
        }

        [Fact]
        public void SwitchSameType_NoConfirm()
        {
            CredentialDraft draft = CredentialDraft.ForNew();
            Assert.False(draft.PreviewSwitch(AuthType.Password).NeedsConfirm);
        }

        [Fact]
        public void ClearPlaintext_WipesFields()
        {
            CredentialDraft draft = CredentialDraft.ForNew();
            draft.SetPassword("p");
            draft.SetPassphrase("ph");
            draft.ClearPlaintext();
            Assert.Equal(string.Empty, draft.Password);
            Assert.Equal(string.Empty, draft.Passphrase);
        }

        [Fact]
        public void FingerprintTail_Last8()
        {
            Assert.Equal("89abcdef", CredentialDraft.FingerprintTail("SHA256:0123456789abcdef"));
            Assert.Equal("abcd", CredentialDraft.FingerprintTail("abcd"));
            Assert.Equal(string.Empty, CredentialDraft.FingerprintTail(null));
        }

        [Fact]
        public void ModifiedEmptyPassword_Removes()
        {
            CredentialDraft draft = CredentialDraft.ForNew();
            draft.HasSavedPassword = true;
            draft.SetPassword("");
            draft.RememberPassword = true;
            Assert.Null(draft.BuildMutations("h1").Single(x => x.Key == SecretKeys.HostPassword("h1")).Value);
        }
    }
}
