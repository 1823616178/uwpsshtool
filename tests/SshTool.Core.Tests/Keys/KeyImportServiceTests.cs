using System.Text;
using System.Threading.Tasks;
using SshTool.Core.Keys;
using SshTool.Core.Models;
using SshTool.Core.Storage;
using SshTool.Core.Storage.Repositories;
using SshTool.Core.Tests.Fakes;
using Xunit;

namespace SshTool.Core.Tests.Keys
{
    // K02 验收：大小上限 / 重复指纹 / 短语错误（含需短语分支与保存往返）。
    public class KeyImportServiceTests
    {
        private sealed class Fixture
        {
            public readonly InMemoryFileSystem Fs = new InMemoryFileSystem();
            public readonly KeyRepository Keys;
            public readonly InMemorySecretStore Secrets = new InMemorySecretStore();
            public readonly FakeKeyTool Tool = new FakeKeyTool();
            public readonly KeyImportService Service;

            public Fixture()
            {
                Keys = new KeyRepository(Fs);
                Service = new KeyImportService(Keys, Secrets, Tool);
            }
        }

        private static string TinyKeyText()
        {
            return "-----BEGIN OPENSSH PRIVATE KEY-----\nfake\n-----END OPENSSH PRIVATE KEY-----\n";
        }

        [Fact]
        public async Task Inspect_Empty_ReturnsEmptyWithoutCallingTool()
        {
            var f = new Fixture();
            KeyInspectOutcome o1 = await f.Service.InspectAsync(null, null);
            KeyInspectOutcome o2 = await f.Service.InspectAsync("   \n ", string.Empty);
            Assert.Equal(KeyInspectStatus.Empty, o1.Status);
            Assert.Equal(KeyInspectStatus.Empty, o2.Status);
            Assert.Equal(0, f.Tool.InspectCount);
        }

        [Fact]
        public async Task Inspect_OverSizeLimit_ReturnsTooLargeWithoutCallingTool()
        {
            var f = new Fixture();
            var sb = new StringBuilder(KeyImportService.MaxPrivateKeyBytes + 1);
            for (int i = 0; i < sb.Capacity; i++)
            {
                sb.Append('a');
            }
            KeyInspectOutcome o = await f.Service.InspectAsync(sb.ToString(), null);
            Assert.Equal(KeyInspectStatus.TooLarge, o.Status);
            Assert.Equal(0, f.Tool.InspectCount);
        }

        [Fact]
        public async Task Inspect_ExactlyAtLimit_CallsTool()
        {
            var f = new Fixture();
            var sb = new StringBuilder(KeyImportService.MaxPrivateKeyBytes);
            for (int i = 0; i < sb.Capacity; i++)
            {
                sb.Append('a');
            }
            KeyInspectOutcome o = await f.Service.InspectAsync(sb.ToString(), null);
            Assert.Equal(KeyInspectStatus.Ready, o.Status);
            Assert.Equal(1, f.Tool.InspectCount);
        }

        [Fact]
        public async Task Inspect_DuplicateFingerprint_ReturnsDuplicateWithExisting()
        {
            var f = new Fixture();
            var seeded = new KeyEntry
            {
                Id = "k-old",
                Name = "旧密钥",
                KeyType = "ssh-ed25519",
                FingerprintSha256 = "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
            };
            await f.Keys.AddAsync(seeded);

            KeyInspectOutcome o = await f.Service.InspectAsync(TinyKeyText(), null);
            Assert.Equal(KeyInspectStatus.Duplicate, o.Status);
            Assert.NotNull(o.Existing);
            Assert.Equal("k-old", o.Existing.Id);
            Assert.NotNull(o.Inspected);
        }

        [Fact]
        public async Task Inspect_ToolReturnsNullWithoutPassphraseAndEncryptedMarker_ReturnsNeedsPassphrase()
        {
            var f = new Fixture();
            f.Tool.InspectFunc = (text, phrase) => null;
            string pem = "-----BEGIN ENCRYPTED PRIVATE KEY-----\nfake\n-----END ENCRYPTED PRIVATE KEY-----\n";

            KeyInspectOutcome o = await f.Service.InspectAsync(pem, null);
            Assert.Equal(KeyInspectStatus.NeedsPassphrase, o.Status);
        }

        [Fact]
        public async Task Inspect_ToolReturnsNullWithPassphrase_ReturnsInvalid()
        {
            var f = new Fixture();
            // 短语错误：native 返回 null，本层不区分「短语错误/文件损坏」，统一 Invalid。
            f.Tool.InspectFunc = (text, phrase) => null;
            string pem = "-----BEGIN ENCRYPTED PRIVATE KEY-----\nfake\n-----END ENCRYPTED PRIVATE KEY-----\n";

            KeyInspectOutcome o = await f.Service.InspectAsync(pem, "wrong-phrase");
            Assert.Equal(KeyInspectStatus.Invalid, o.Status);
        }

        [Fact]
        public async Task Inspect_PartialEncryptedWithoutFingerprint_ReturnsNeedsPassphrase()
        {
            var f = new Fixture();
            f.Tool.InspectFunc = (text, phrase) => new InspectedKeyInfo
            {
                KeyType = string.Empty,
                Bits = 0,
                Format = "pem",
                Encrypted = true,
                PublicKeyOpenSsh = string.Empty,
                FingerprintSha256 = string.Empty,
                Comment = string.Empty
            };

            KeyInspectOutcome o = await f.Service.InspectAsync(TinyKeyText(), null);
            Assert.Equal(KeyInspectStatus.NeedsPassphrase, o.Status);
            Assert.NotNull(o.Inspected);
            Assert.True(o.Inspected.Encrypted);
        }

        [Fact]
        public async Task Inspect_CorrectPassphraseAfterNeedsPassphrase_ReturnsReady()
        {
            var f = new Fixture();
            f.Tool.InspectFunc = (text, phrase) =>
                phrase == "right-phrase" ? new FakeKeyTool().DefaultInfo.Clone() : null;
            string pem = "-----BEGIN ENCRYPTED PRIVATE KEY-----\nfake\n-----END ENCRYPTED PRIVATE KEY-----\n";

            KeyInspectOutcome need = await f.Service.InspectAsync(pem, null);
            Assert.Equal(KeyInspectStatus.NeedsPassphrase, need.Status);
            KeyInspectOutcome ready = await f.Service.InspectAsync(pem, "right-phrase");
            Assert.Equal(KeyInspectStatus.Ready, ready.Status);
        }

        [Fact]
        public async Task Save_WritesMetadataAndSecrets()
        {
            var f = new Fixture();
            KeyInspectOutcome o = await f.Service.InspectAsync(TinyKeyText(), null);
            Assert.Equal(KeyInspectStatus.Ready, o.Status);

            KeyEntry entry = await f.Service.SaveAsync(TinyKeyText(), "我的密钥", o.Inspected, "key-phrase");
            Assert.False(string.IsNullOrEmpty(entry.Id));
            Assert.Equal("我的密钥", entry.Name);
            Assert.Equal("ssh-ed25519", entry.KeyType);
            Assert.Equal(
                "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                (await f.Keys.GetByIdAsync(entry.Id)).FingerprintSha256);
            Assert.Equal(TinyKeyText(), await f.Secrets.GetAsync(SecretKeys.KeyPrivate(entry.Id)));
            Assert.Equal("key-phrase", await f.Secrets.GetAsync(SecretKeys.KeyPassphrase(entry.Id)));
        }

        [Fact]
        public async Task Save_WithoutPassphrase_DoesNotStorePassphrase()
        {
            var f = new Fixture();
            KeyInspectOutcome o = await f.Service.InspectAsync(TinyKeyText(), null);
            KeyEntry entry = await f.Service.SaveAsync(TinyKeyText(), "n", o.Inspected, null);
            Assert.Null(await f.Secrets.GetAsync(SecretKeys.KeyPassphrase(entry.Id)));
        }

        [Fact]
        public async Task Generate_Ed25519_SavesAndReturnsEntry()
        {
            var f = new Fixture();
            KeyGenerateOutcome o = await f.Service.GenerateAsync(KeyGenerateKind.Ed25519, 0, "c", "gen");
            Assert.True(o.Success);
            Assert.NotNull(o.Entry);
            Assert.Equal("fake-ed25519-private", o.PrivateKeyText);
            Assert.NotNull(await f.Keys.GetByIdAsync(o.Entry.Id));
            Assert.Equal("fake-ed25519-private", await f.Secrets.GetAsync(SecretKeys.KeyPrivate(o.Entry.Id)));
        }

        [Fact]
        public async Task Generate_RsaBadBits_Refused()
        {
            var f = new Fixture();
            KeyGenerateOutcome o = await f.Service.GenerateAsync(KeyGenerateKind.Rsa, 2048, "c", "gen");
            Assert.False(o.Success);
            Assert.False(string.IsNullOrEmpty(o.Error));
            Assert.Equal(KeyGenerateError.UnsupportedBits, o.ErrorCode);
        }

        [Fact]
        public async Task Generate_ToolFailure_ReturnsError()
        {
            var f = new Fixture();
            f.Tool.FailGenerate = true;
            KeyGenerateOutcome o = await f.Service.GenerateAsync(KeyGenerateKind.Ed25519, 0, "c", "gen");
            Assert.False(o.Success);
        }
    }
}
