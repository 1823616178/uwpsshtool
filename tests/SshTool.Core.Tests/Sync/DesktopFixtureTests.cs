using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using SshTool.Core.Models;
using SshTool.Core.Sync.Protocol;
using Xunit;

namespace SshTool.Core.Tests.Sync
{
    // S04 验收 §10.1 Serializer 5：桌面端真实导出的文档夹具（tools/sync-vectors 生成，
    // 与桌面端 crypto-vault.ts 同原语加密并经桌面端 zod 自检）解析成功且重新序列化稳定。
    // 跨端校验的另一半（重写输出经桌面端 zod）由 scripts/verify.ps1 -Interop 执行：
    // 本测试顺带把重写输出落盘到 artifacts/sync-interop/ 供 validate-fixtures.mjs 校验。
    public class DesktopFixtureTests
    {
        public static IEnumerable<object[]> DesktopDocuments()
        {
            yield return new object[] { "desktop-document-empty.json" };
            yield return new object[] { "desktop-document-typical.json" };
            yield return new object[] { "desktop-document-secrets.json" };
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "tests", "fixtures", "sync", "desktop-vectors.json")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            throw new FileNotFoundException("找不到仓库根（tests/fixtures/sync/desktop-vectors.json 缺失）");
        }

        private static string Fixture(string name)
        {
            string path = Path.Combine(RepoRoot(), "tests", "fixtures", "sync", name);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("找不到夹具 " + name);
            }
            return File.ReadAllText(path, new UTF8Encoding(false));
        }

        private static string Sha256Hex(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(data);
                var sb = new StringBuilder(digest.Length * 2);
                foreach (byte b in digest)
                {
                    sb.Append(b.ToString("x2"));
                }
                return sb.ToString();
            }
        }

        // 夹具经 Reader→Writer 后字节稳定；且因夹具已是 canonical 形态，重写必须逐字节相等
        // （证明 JS canonicalizer 与 C# Writer 一致；verify 再用桌面端 zod 校验重写输出）。
        [Theory]
        [MemberData(nameof(DesktopDocuments))]
        public void DesktopDocument_RoundTrip_ByteStable(string name)
        {
            string original = Fixture(name).Trim();
            var doc = SyncDocumentReader.Read(original);
            string rewritten = SyncDocumentWriter.Write(doc);
            Assert.Equal(original, rewritten);

            string rewritten2 = SyncDocumentWriter.Write(SyncDocumentReader.Read(rewritten));
            Assert.Equal(rewritten, rewritten2);
            Assert.True(SyncDocumentWriter.SameContent(doc, SyncDocumentReader.Read(rewritten)));

            // 落盘供 verify.ps1 -Interop 做桌面端 zod 校验（Reader→Writer→validate-fixtures.mjs）。
            string outDir = Path.Combine(RepoRoot(), "artifacts", "sync-interop");
            Directory.CreateDirectory(outDir);
            File.WriteAllText(
                Path.Combine(outDir, name.Replace(".json", ".rewritten.json")),
                rewritten,
                new UTF8Encoding(false));

            // 内容锚点：防止夹具被意外替换为空壳仍“自洽通过”。
            if (name == "desktop-document-empty.json")
            {
                Assert.Empty(doc.Servers);
                Assert.Empty(doc.Tunnels);
                Assert.Empty(doc.Groups);
            }
            else if (name == "desktop-document-typical.json")
            {
                Assert.Equal(2, doc.Servers.Count);
                Assert.Equal(2, doc.Tunnels.Count);
                Assert.Equal(2, doc.Groups.Count);
                bool hasRelay = false;
                foreach (var t in doc.Tunnels)
                {
                    if (t.Type == TunnelType.Relay)
                    {
                        hasRelay = true;
                        Assert.Equal("srv-01", t.DestServerId);
                    }
                }
                Assert.True(hasRelay, "typical 夹具应含 relay 隧道");
            }
            else if (name == "desktop-document-secrets.json")
            {
                Assert.Single(doc.Servers);
                Assert.NotNull(doc.Servers[0].Secrets);
                Assert.Equal("S04-Test-Password-123", doc.Servers[0].Secrets.Password);
                Assert.True(doc.Preferences.SyncPasswords);
            }
        }

        // 向量一致性（不做 Argon2/AES，密码学由 native desktop_vectors_test 覆盖）：
        // 信封字段均为规范 Base64；文档 seals 的 ciphertextHash == sha256(密文)；
        // 明文 sha256 与文档夹具字节一致。
        [Fact]
        public void DesktopVectors_HashesAndEncodingsConsistent()
        {
            var vectors = JObject.Parse(Fixture("desktop-vectors.json"));
            Assert.Equal("4.12.0", (string)vectors["hashWasm"]);
            Assert.Equal("4.12.0", (string)vectors["desktopLockHashWasm"]);

            var kdf = (JObject)vectors["kdfParameters"];
            Assert.Equal("argon2id", (string)kdf["algorithm"]);
            Assert.Equal(65536, (int)kdf["memory"]);
            Assert.Equal(3, (int)kdf["iterations"]);
            Assert.Equal(1, (int)kdf["parallelism"]);

            var envelope = (JObject)vectors["envelope"];
            foreach (string key in new[] { "passwordWrappedKey", "passwordWrapNonce", "recoveryWrappedKey", "recoveryWrapNonce", "kdfSalt" })
            {
                string value = (string)envelope[key];
                Assert.NotNull(CanonicalBase64.TryDecode(value));
            }

            var documents = (JObject)vectors["documents"];
            var nameMap = new Dictionary<string, string>
            {
                { "empty", "desktop-document-empty.json" },
                { "typical", "desktop-document-typical.json" },
                { "secrets", "desktop-document-secrets.json" },
            };
            foreach (var kv in nameMap)
            {
                var entry = (JObject)documents[kv.Key];
                var seal = (JObject)entry["envelope"];
                Assert.Equal("AES-256-GCM", (string)seal["algorithm"]);

                byte[] ct = CanonicalBase64.TryDecode((string)entry["ciphertext"]);
                Assert.NotNull(ct);
                Assert.Equal((string)entry["ciphertextHash"], Sha256Hex(ct));

                byte[] nonce = CanonicalBase64.TryDecode((string)entry["nonce"]);
                Assert.NotNull(nonce);
                Assert.Equal(12, nonce.Length);

                string fixtureText = Fixture(kv.Value).Trim();
                byte[] ptBytes = new UTF8Encoding(false).GetBytes(fixtureText);
                Assert.Equal((string)entry["plaintextSha256"], Sha256Hex(ptBytes));
            }
        }
    }
}
