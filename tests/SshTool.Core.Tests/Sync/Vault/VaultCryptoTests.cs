using System.Text;
using System.Threading.Tasks;
using SshTool.Core.Sync.Api.Dtos;
using SshTool.Core.Sync.Vault;
using SshTool.Core.Tests.Fakes;
using Xunit;

namespace SshTool.Core.Tests.Sync.Vault
{
    // S05：信封模型（往返/拒绝/Clone）与 FakeVaultCrypto（可逆模拟/密码错误）。
    public class VaultCryptoTests
    {
        private static VaultKeyEnvelope SampleEnvelope()
        {
            return new VaultKeyEnvelope
            {
                KeyVersion = 1,
                PasswordWrappedKey = "cHcx",
                PasswordWrapNonce = "bm9uY2U",
                RecoveryWrappedKey = "cmMx",
                RecoveryWrapNonce = "bm9uY2Uy",
                KdfSalt = "c2FsdA",
                KdfAlgorithm = "argon2id",
                KdfMemory = 65536,
                KdfIterations = 3,
                KdfParallelism = 1
            };
        }

        [Fact]
        public void VaultKeyEnvelope_ToJson_Parse_RoundTrip()
        {
            VaultKeyEnvelope back = VaultKeyEnvelope.Parse(SampleEnvelope().ToJson());
            Assert.Equal(1, back.KeyVersion);
            Assert.Equal("cHcx", back.PasswordWrappedKey);
            Assert.Equal("bm9uY2U", back.PasswordWrapNonce);
            Assert.Equal("cmMx", back.RecoveryWrappedKey);
            Assert.Equal("bm9uY2Uy", back.RecoveryWrapNonce);
            Assert.Equal("c2FsdA", back.KdfSalt);
            Assert.Equal("argon2id", back.KdfAlgorithm);
            Assert.Equal(65536, back.KdfMemory);
            Assert.Equal(3, back.KdfIterations);
            Assert.Equal(1, back.KdfParallelism);
        }

        [Fact]
        public void VaultKeyEnvelope_Parse_RejectsMissingKey()
        {
            var json = SampleEnvelope().ToJson();
            json.Remove("kdfSalt");
            Assert.Throws<ProtocolParseException>(() => VaultKeyEnvelope.Parse(json));
        }

        [Theory]
        [InlineData("argon2i", 65536, 3, 1)]   // 算法不对
        [InlineData("argon2id", 100, 3, 1)]    // memory 越下界
        [InlineData("argon2id", 65536, 0, 1)]  // iterations 越下界
        [InlineData("argon2id", 65536, 3, 17)] // parallelism 越上界
        public void VaultKeyEnvelope_Parse_RejectsBadKdf(string algo, int mem, int iter, int par)
        {
            var envelope = SampleEnvelope();
            envelope.KdfAlgorithm = algo;
            envelope.KdfMemory = mem;
            envelope.KdfIterations = iter;
            envelope.KdfParallelism = par;
            Assert.Throws<ProtocolParseException>(() => VaultKeyEnvelope.Parse(envelope.ToJson()));
        }

        [Fact]
        public void VaultKeyEnvelope_Parse_RejectsZeroKeyVersion()
        {
            var envelope = SampleEnvelope();
            envelope.KeyVersion = 0;
            Assert.Throws<ProtocolParseException>(() => VaultKeyEnvelope.Parse(envelope.ToJson()));
        }

        [Fact]
        public void VaultKeyEnvelope_Clone_IsIndependent()
        {
            VaultKeyEnvelope original = SampleEnvelope();
            VaultKeyEnvelope clone = original.Clone();
            clone.PasswordWrappedKey = "changed";
            clone.KdfMemory = 8192;
            Assert.Equal("cHcx", original.PasswordWrappedKey);
            Assert.Equal(65536, original.KdfMemory);
        }

        [Fact]
        public void DocumentEnvelope_ToJson_Parse_RoundTrip()
        {
            var envelope = new EncryptedDocumentEnvelope
            {
                SchemaVersion = 1,
                KeyVersion = 2,
                Algorithm = "AES-256-GCM",
                Nonce = "bm9uY2U",
                Ciphertext = "Y2lwaGVy",
                CiphertextHash = "aGFzaA"
            };
            EncryptedDocumentEnvelope back = EncryptedDocumentEnvelope.Parse(envelope.ToJson());
            Assert.Equal(1, back.SchemaVersion);
            Assert.Equal(2, back.KeyVersion);
            Assert.Equal("AES-256-GCM", back.Algorithm);
            Assert.Equal("bm9uY2U", back.Nonce);
            Assert.Equal("Y2lwaGVy", back.Ciphertext);
            Assert.Equal("aGFzaA", back.CiphertextHash);
        }

        [Fact]
        public void DocumentEnvelope_Parse_RejectsWrongAlgorithm()
        {
            var envelope = new EncryptedDocumentEnvelope
            {
                SchemaVersion = 1,
                KeyVersion = 1,
                Algorithm = "AES-256-CBC",
                Nonce = "bm9uY2U",
                Ciphertext = "Y2lwaGVy",
                CiphertextHash = "aGFzaA"
            };
            Assert.Throws<ProtocolParseException>(() => EncryptedDocumentEnvelope.Parse(envelope.ToJson()));
        }

        [Fact]
        public void DocumentEnvelope_Clone_IsIndependent()
        {
            var original = new EncryptedDocumentEnvelope
            {
                SchemaVersion = 1,
                KeyVersion = 1,
                Algorithm = "AES-256-GCM",
                Nonce = "bm9uY2U",
                Ciphertext = "Y2lwaGVy",
                CiphertextHash = "aGFzaA"
            };
            EncryptedDocumentEnvelope clone = original.Clone();
            clone.Ciphertext = "changed";
            Assert.Equal("Y2lwaGVy", original.Ciphertext);
        }

        // ---------- FakeVaultCrypto ----------

        [Fact]
        public async Task Fake_Create_UnwrapPassword_RoundTrip()
        {
            var crypto = new FakeVaultCrypto();
            VaultSetupResult setup = await crypto.CreateAsync("pw-123", 1);
            Assert.NotNull(setup);
            Assert.NotNull(setup.Envelope);
            Assert.False(string.IsNullOrEmpty(setup.VaultKeyBase64));
            Assert.False(string.IsNullOrEmpty(setup.RecoveryKey));
            string key = await crypto.UnwrapWithPasswordAsync(setup.Envelope, "pw-123");
            Assert.Equal(setup.VaultKeyBase64, key);
        }

        [Fact]
        public async Task Fake_UnwrapPassword_WrongPassword_ReturnsNull()
        {
            var crypto = new FakeVaultCrypto();
            VaultSetupResult setup = await crypto.CreateAsync("pw-123", 1);
            Assert.Null(await crypto.UnwrapWithPasswordAsync(setup.Envelope, "wrong"));
        }

        [Fact]
        public async Task Fake_FailPasswordUnwrap_SimulatesPasswordError()
        {
            var crypto = new FakeVaultCrypto { FailPasswordUnwrap = true };
            VaultSetupResult setup = await crypto.CreateAsync("pw-123", 1);
            Assert.Null(await crypto.UnwrapWithPasswordAsync(setup.Envelope, "pw-123"));
        }

        [Fact]
        public async Task Fake_Recovery_RoundTrip_And_WrongKey_ReturnsNull()
        {
            var crypto = new FakeVaultCrypto();
            VaultSetupResult setup = await crypto.CreateAsync("pw-123", 1);
            string key = await crypto.UnwrapWithRecoveryKeyAsync(setup.Envelope, setup.RecoveryKey);
            Assert.Equal(setup.VaultKeyBase64, key);
            Assert.Null(await crypto.UnwrapWithRecoveryKeyAsync(setup.Envelope, "SPM1-bad-key-000000000000"));
        }

        [Fact]
        public async Task Fake_Encrypt_Decrypt_RoundTrip_WithUnicode()
        {
            var crypto = new FakeVaultCrypto();
            VaultSetupResult setup = await crypto.CreateAsync("pw-123", 1);
            byte[] plaintext = Encoding.UTF8.GetBytes("{\"a\":1} 中文 🎉");
            EncryptedDocumentEnvelope env = await crypto.EncryptDocumentAsync(
                setup.VaultKeyBase64, "vault-1", 1, 1, plaintext);
            Assert.NotNull(env);
            byte[] back = await crypto.DecryptDocumentAsync(setup.VaultKeyBase64, "vault-1", env);
            Assert.Equal(plaintext, back);
        }

        [Fact]
        public async Task Fake_Decrypt_TamperedCiphertext_ReturnsNull()
        {
            var crypto = new FakeVaultCrypto();
            VaultSetupResult setup = await crypto.CreateAsync("pw-123", 1);
            EncryptedDocumentEnvelope env = await crypto.EncryptDocumentAsync(
                setup.VaultKeyBase64, "vault-1", 1, 1, Encoding.UTF8.GetBytes("hello"));
            env.Ciphertext = FlipFirstChar(env.Ciphertext);
            Assert.Null(await crypto.DecryptDocumentAsync(setup.VaultKeyBase64, "vault-1", env));
        }

        [Fact]
        public async Task Fake_Decrypt_WrongVaultIdOrKey_ReturnsNull()
        {
            var crypto = new FakeVaultCrypto();
            VaultSetupResult setup = await crypto.CreateAsync("pw-123", 1);
            EncryptedDocumentEnvelope env = await crypto.EncryptDocumentAsync(
                setup.VaultKeyBase64, "vault-1", 1, 1, Encoding.UTF8.GetBytes("hello"));
            Assert.Null(await crypto.DecryptDocumentAsync(setup.VaultKeyBase64, "vault-2", env));
            Assert.Null(await crypto.DecryptDocumentAsync("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", "vault-1", env));
        }

        [Fact]
        public async Task Fake_Create_NullPassword_ReturnsNull()
        {
            var crypto = new FakeVaultCrypto();
            Assert.Null(await crypto.CreateAsync(null, 1));
            Assert.Null(await crypto.CreateAsync("pw", 0));
        }

        [Fact]
        public async Task Fake_Encrypt_NullPlaintext_ReturnsNull()
        {
            var crypto = new FakeVaultCrypto();
            VaultSetupResult setup = await crypto.CreateAsync("pw-123", 1);
            Assert.Null(await crypto.EncryptDocumentAsync(setup.VaultKeyBase64, "vault-1", 1, 1, null));
            Assert.Null(await crypto.DecryptDocumentAsync(setup.VaultKeyBase64, "vault-1", null));
        }

        private static string FlipFirstChar(string b64)
        {
            char first = b64[0] == 'A' ? 'B' : 'A';
            return first + b64.Substring(1);
        }
    }
}
