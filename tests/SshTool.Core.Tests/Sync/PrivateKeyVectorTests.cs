using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using SshTool.Core.Sync.Protocol;
using Xunit;

namespace SshTool.Core.Tests.Sync
{
    // S15 验收：ed25519/rsa/ecdsa × openssh/pem × 加密/未加密 的指纹与桌面端一致。
    // 期望值由 tools/sync-vectors/private-keys.mjs 经桌面端 node_modules/ssh2 的
    // utils.parseKey().getPublicSSH() 生成（PKCS#8 两类 ssh2 不支持，取自 .pub，
    // 见该脚本文件头），本测试做 C# 可达的交叉核对：
    // format 判定（纯函数）、夹具哈希（防夹具漂移）、指纹形状；
    // 指纹值本身的 native 侧断言在 keytool_test.cpp KeytoolInteropTest。
    public class PrivateKeyVectorTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "tests", "fixtures", "sync", "private-key-vectors.json")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            throw new FileNotFoundException("找不到仓库根（tests/fixtures/sync/private-key-vectors.json 缺失）");
        }

        private static JObject Vectors()
        {
            string path = Path.Combine(RepoRoot(), "tests", "fixtures", "sync", "private-key-vectors.json");
            return JObject.Parse(File.ReadAllText(path, new UTF8Encoding(false)));
        }

        public static IEnumerable<object[]> Entries()
        {
            var entries = (JArray)Vectors()["entries"];
            foreach (var e in entries)
            {
                yield return new object[]
                {
                    (string)e["file"], (string)e["keyType"], (string)e["format"],
                    (bool)e["encrypted"], (string)e["fingerprint"], (string)e["sha256Hex"]
                };
            }
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

        // 向量条目与夹具目录精确对应（无遗漏、无多余；文件名即冻结契约）。
        [Fact]
        public void EntriesMatchFixtureDirectory()
        {
            var listed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in Entries())
            {
                listed.Add((string)row[0]);
            }
            string dir = Path.Combine(RepoRoot(), "native", "tests", "fixtures", "keys");
            var onDisk = new HashSet<string>(Directory.GetFiles(dir)
                .Select(Path.GetFileName)
                .Where(n => !n.EndsWith(".pub", StringComparison.Ordinal)),
                StringComparer.Ordinal);
            Assert.Equal(onDisk, listed);
            // ed25519/rsa/ecdsa × openssh/pem 的类型覆盖不断档。
            var formats = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in Entries())
            {
                formats.Add((string)row[2]);
            }
            Assert.Contains("openssh", formats);
            Assert.Contains("pem", formats);
        }

        // 每条目：C# header 判定 == 向量 format；夹具哈希 == 向量 sha256Hex；
        // 指纹形状合法；encrypted 标记与 _enc 命名约定一致。
        [Theory]
        [MemberData(nameof(Entries))]
        public void EntryMatchesLocalChecks(string file, string keyType, string format,
            bool encrypted, string fingerprint, string sha256Hex)
        {
            Assert.False(string.IsNullOrEmpty(keyType));
            Assert.True(format == "openssh" || format == "pem", "format 非法：" + format);
            Assert.True(PrivateKeyFormat.IsValidFingerprint(fingerprint), "指纹形状非法：" + file);
            Assert.Equal(encrypted, file.EndsWith("_enc", StringComparison.Ordinal));

            string path = Path.Combine(RepoRoot(), "native", "tests", "fixtures", "keys", file);
            byte[] raw = File.ReadAllBytes(path);
            Assert.Equal(sha256Hex, Sha256Hex(raw));
            Assert.Equal(format, PrivateKeyFormat.Detect(raw));
        }
    }
}
