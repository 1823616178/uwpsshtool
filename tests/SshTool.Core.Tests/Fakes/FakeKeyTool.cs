using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SshTool.Core.Keys;

namespace SshTool.Core.Tests.Fakes
{
    // K02：IKeyTool 的假实现（供 KeyImportService 单测）。
    //
    // Inspect 按注入的 InspectFunc 裁决（默认返回 DefaultInfo 的克隆）；
    // Generate 返回注入的文本（默认固定文本）。调用记录在 Calls 与 Last*。
    public sealed class FakeKeyTool : IKeyTool
    {
        public readonly List<string> Calls = new List<string>();
        public string LastInspectText;
        public string LastPassphrase;
        public int InspectCount;

        // 返回 null 即模拟「解析失败」（短语错误/损坏文件，native 语义一致）。
        public Func<string, string, InspectedKeyInfo> InspectFunc;

        public string Ed25519PrivateText = "fake-ed25519-private";
        public string RsaPrivateText = "fake-rsa-private";
        public bool FailGenerate;

        public InspectedKeyInfo DefaultInfo = new InspectedKeyInfo
        {
            KeyType = "ssh-ed25519",
            Bits = 256,
            Format = "openssh",
            Encrypted = false,
            PublicKeyOpenSsh = "ssh-ed25519 AAAAC3Fake comment",
            FingerprintSha256 = "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            Comment = "comment"
        };

        public Task<InspectedKeyInfo> InspectAsync(string privateKeyText, string passphrase)
        {
            Calls.Add("Inspect");
            LastInspectText = privateKeyText;
            LastPassphrase = passphrase;
            InspectCount++;
            if (InspectFunc != null)
            {
                return Task.FromResult(InspectFunc(privateKeyText, passphrase));
            }
            return Task.FromResult(DefaultInfo.Clone());
        }

        public Task<string> GenerateEd25519Async(string comment)
        {
            Calls.Add("GenerateEd25519");
            if (FailGenerate)
            {
                return Task.FromResult<string>(null);
            }
            return Task.FromResult(Ed25519PrivateText);
        }

        public Task<string> GenerateRsaAsync(int bits, string comment)
        {
            Calls.Add("GenerateRsa");
            if (FailGenerate)
            {
                return Task.FromResult<string>(null);
            }
            return Task.FromResult(RsaPrivateText);
        }
    }
}
