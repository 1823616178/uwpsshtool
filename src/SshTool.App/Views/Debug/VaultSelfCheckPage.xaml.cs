using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SshTool.App.Platform;
using SshTool.Core.Sync.Vault;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Views.Debug
{
    // S05：保险库自检页（真机验收：黄金向量 + 桌面向量 + Argon2 耗时）。
    //
    // 走 App/Platform/NativeVaultCrypto（真 native：OpenSSL + libargon2），
    // 不走 Fake。报告只含 PASS/FAIL、向量名、字节数与耗时毫秒——
    // 绝不含测试口令、恢复密钥、vaultKey、明文与密文（日志脱敏）。
    public sealed partial class VaultSelfCheckPage : Page
    {
        // 仅自检用口令：进内存调 Create/Unwrap，不持久化、不进报告不进日志。
        private const string TestPassword = "s05-selfcheck-password";
        private const string TestVaultId = "vault-selfcheck";

        // 黄金向量（native/tests/vault_golden_vectors.h，原样复制的冻结值，
        // 由桌面端 hash-wasm Argon2id + Node crypto 独立算出）：
        // 只取「文档解密已知答案」部分（vaultKey/固定 nonce/密文/hash），
        // 透过桥 DecryptDocumentAsync 验证 AES-GCM + AAD + ciphertextHash 全链路。
        private const string GoldenVaultKeyHex =
            "86b05fc972d63bcc7c33b0df2420f23484adbab54b923533983ec93b9783a7ec";
        private const string GoldenNonceHex = "101112131415161718191a1b";
        private const string GoldenCiphertextHex =
            "636005c373efaad17c396e443e30b26fde0c00ab78b6442183407832d6f8acb47fe631";
        private const string GoldenCiphertextHash =
            "7642985e1b19ef5784767564e06c2dce7c3ca7322acbf33865c1de03bf9a73c8";
        private const string GoldenVaultId = "vault-golden";
        private const string GoldenPlaintext = "s2-golden-plaintext";

        private const string VectorsAssetUri =
            "ms-appx:///Assets/sync-vectors/vault-selfcheck-vectors.json";

        private readonly NativeVaultCrypto _crypto = new NativeVaultCrypto();
        private string _lastReport = string.Empty;

        public VaultSelfCheckPage()
        {
            this.InitializeComponent();
        }

        private async void OnRunClick(object sender, RoutedEventArgs e)
        {
            RunButton.IsEnabled = false;
            CopyButton.IsEnabled = false;
            ReportText.Text = "运行中…（解锁含两次 Argon2id，真机约数秒）";
            StatusText.Text = "运行中…";
            try
            {
                var lines = new List<string>();
                lines.Add(DebugReport.EnvironmentHeader());
                lines.Add("S05 保险库自检（native 真实现）");
                int passed = 0;
                int total = 0;

                // 1. 黄金向量解密
                total++;
                if (await CheckGoldenDecryptAsync(lines))
                {
                    passed++;
                }

                // 2. 黄金向量篡改必须被拒
                total++;
                if (await CheckGoldenTamperAsync(lines))
                {
                    passed++;
                }

                // 3–7. 创建/解包/恢复/文档往返/错误口令（含 Argon2 耗时）
                var roundtrip = await CheckRoundtripAsync(lines);
                total += roundtrip.Item1;
                passed += roundtrip.Item2;

                // 8. 桌面向量文件（S04 风格的固定向量；缺失则 SKIP，不判失败）
                total++;
                if (await CheckDesktopVectorsAsync(lines))
                {
                    passed++;
                }

                lines.Add(string.Format(CultureInfo.InvariantCulture,
                    "汇总：{0}/{1} 通过", passed, total));
                _lastReport = string.Join("\n", lines);
                ReportText.Text = _lastReport;
                CopyButton.IsEnabled = true;
                StatusText.Text = await DebugReport.PublishAsync("vault-selfcheck", "S05", _lastReport);
            }
            catch (Exception ex)
            {
                ReportText.Text = "自检异常：" + ex.GetType().Name;
                StatusText.Text = "失败";
            }
            finally
            {
                RunButton.IsEnabled = true;
            }
        }

        private async Task<bool> CheckGoldenDecryptAsync(List<string> lines)
        {
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                var envelope = new EncryptedDocumentEnvelope
                {
                    SchemaVersion = 1,
                    KeyVersion = 1,
                    Algorithm = "AES-256-GCM",
                    Nonce = Convert.ToBase64String(HexToBytes(GoldenNonceHex)),
                    Ciphertext = Convert.ToBase64String(HexToBytes(GoldenCiphertextHex)),
                    CiphertextHash = GoldenCiphertextHash
                };
                string keyB64 = Convert.ToBase64String(HexToBytes(GoldenVaultKeyHex));
                byte[] plaintext = await _crypto.DecryptDocumentAsync(keyB64, GoldenVaultId, envelope)
                    .ConfigureAwait(false);
                bool ok = plaintext != null
                    && Encoding.UTF8.GetString(plaintext, 0, plaintext.Length) == GoldenPlaintext;
                watch.Stop();
                lines.Add(Format(ok, "黄金向量解密", watch));
                return ok;
            }
            catch
            {
                watch.Stop();
                lines.Add(Format(false, "黄金向量解密", watch));
                return false;
            }
        }

        private async Task<bool> CheckGoldenTamperAsync(List<string> lines)
        {
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                string ciphertext = Convert.ToBase64String(HexToBytes(GoldenCiphertextHex));
                char flipped = ciphertext[0] == 'A' ? 'B' : 'A';
                var envelope = new EncryptedDocumentEnvelope
                {
                    SchemaVersion = 1,
                    KeyVersion = 1,
                    Algorithm = "AES-256-GCM",
                    Nonce = Convert.ToBase64String(HexToBytes(GoldenNonceHex)),
                    Ciphertext = flipped + ciphertext.Substring(1),
                    CiphertextHash = GoldenCiphertextHash
                };
                string keyB64 = Convert.ToBase64String(HexToBytes(GoldenVaultKeyHex));
                byte[] plaintext = await _crypto.DecryptDocumentAsync(keyB64, GoldenVaultId, envelope)
                    .ConfigureAwait(false);
                bool ok = plaintext == null;
                watch.Stop();
                lines.Add(Format(ok, "黄金向量篡改被拒", watch));
                return ok;
            }
            catch
            {
                watch.Stop();
                lines.Add(Format(false, "黄金向量篡改被拒", watch));
                return false;
            }
        }

        // 返回 Tuple(总数, 通过数)。C# 7.3：用 Tuple.Create。
        private async Task<Tuple<int, int>> CheckRoundtripAsync(List<string> lines)
        {
            int total = 0;
            int passed = 0;

            // 创建（第 1 次 Argon2）与密码解包（第 2 次 Argon2）：耗时即 Argon2 耗时。
            total++;
            Stopwatch createWatch = Stopwatch.StartNew();
            VaultSetupResult setup = await _crypto.CreateAsync(TestPassword, 1).ConfigureAwait(false);
            createWatch.Stop();
            bool created = setup != null && setup.Envelope != null
                && !string.IsNullOrEmpty(setup.VaultKeyBase64)
                && !string.IsNullOrEmpty(setup.RecoveryKey);
            lines.Add(Format(created, "创建保险库（Argon2 耗时 "
                + createWatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + "ms）", createWatch));
            if (created)
            {
                passed++;
            }
            else
            {
                return Tuple.Create(total, passed);
            }

            total++;
            Stopwatch unwrapWatch = Stopwatch.StartNew();
            string key = await _crypto.UnwrapWithPasswordAsync(setup.Envelope, TestPassword)
                .ConfigureAwait(false);
            unwrapWatch.Stop();
            bool unwrapped = string.Equals(key, setup.VaultKeyBase64, StringComparison.Ordinal);
            lines.Add(Format(unwrapped, "密码解包一致（Argon2 耗时 "
                + unwrapWatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + "ms）", unwrapWatch));
            if (unwrapped)
            {
                passed++;
            }

            total++;
            string wrong = await _crypto.UnwrapWithPasswordAsync(setup.Envelope, TestPassword + "-wrong")
                .ConfigureAwait(false);
            bool wrongRejected = wrong == null;
            lines.Add(Format(wrongRejected, "错误口令被拒", null));
            if (wrongRejected)
            {
                passed++;
            }

            total++;
            string recovered = await _crypto.UnwrapWithRecoveryKeyAsync(setup.Envelope, setup.RecoveryKey)
                .ConfigureAwait(false);
            bool recoveryOk = string.Equals(recovered, setup.VaultKeyBase64, StringComparison.Ordinal);
            lines.Add(Format(recoveryOk, "恢复密钥解包一致", null));
            if (recoveryOk)
            {
                passed++;
            }

            total++;
            string badRecovery = await _crypto.UnwrapWithRecoveryKeyAsync(
                setup.Envelope, "SPM1-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA-000000000000")
                .ConfigureAwait(false);
            bool badRecoveryRejected = badRecovery == null;
            lines.Add(Format(badRecoveryRejected, "无效恢复密钥被拒", null));
            if (badRecoveryRejected)
            {
                passed++;
            }

            total++;
            byte[] plaintext = Encoding.UTF8.GetBytes("{\"hello\":\"你好 🎉\"}");
            EncryptedDocumentEnvelope doc = await _crypto.EncryptDocumentAsync(
                setup.VaultKeyBase64, TestVaultId, 1, 1, plaintext).ConfigureAwait(false);
            byte[] back = doc == null ? null
                : await _crypto.DecryptDocumentAsync(setup.VaultKeyBase64, TestVaultId, doc)
                    .ConfigureAwait(false);
            bool docOk = back != null && back.Length == plaintext.Length;
            if (docOk)
            {
                for (int i = 0; i < back.Length; i++)
                {
                    if (back[i] != plaintext[i])
                    {
                        docOk = false;
                        break;
                    }
                }
            }
            lines.Add(Format(docOk, "文档加解密往返", null));
            if (docOk)
            {
                passed++;
            }

            total++;
            byte[] wrongVault = doc == null ? new byte[0]
                : await _crypto.DecryptDocumentAsync(setup.VaultKeyBase64, "vault-other", doc)
                    .ConfigureAwait(false);
            bool wrongVaultRejected = wrongVault == null;
            lines.Add(Format(wrongVaultRejected, "错误 vaultId 被拒", null));
            if (wrongVaultRejected)
            {
                passed++;
            }

            return Tuple.Create(total, passed);
        }

        private async Task<bool> CheckDesktopVectorsAsync(List<string> lines)
        {
            Stopwatch watch = Stopwatch.StartNew();
            JObject root;
            try
            {
                StorageFile file = await StorageFile.GetFileFromApplicationUriAsync(new Uri(VectorsAssetUri));
                string json = await FileIO.ReadTextAsync(file);
                root = JObject.Parse(json);
            }
            catch
            {
                watch.Stop();
                lines.Add("SKIP 桌面向量文件缺失（" + VectorsAssetUri + "）");
                return true;
            }
            try
            {
                JArray items = (JArray)root["vectors"];
                if (items == null || items.Count == 0)
                {
                    lines.Add(Format(false, "桌面向量为空", watch));
                    return false;
                }
                bool allOk = true;
                foreach (JToken item in items)
                {
                    string name = (string)item["name"] ?? "?";
                    var envelope = new EncryptedDocumentEnvelope
                    {
                        SchemaVersion = (int)item["schemaVersion"],
                        KeyVersion = (int)item["keyVersion"],
                        Algorithm = (string)item["algorithm"],
                        Nonce = (string)item["nonce"],
                        Ciphertext = (string)item["ciphertext"],
                        CiphertextHash = (string)item["ciphertextHash"]
                    };
                    byte[] expected = Convert.FromBase64String((string)item["plaintextB64"]);
                    byte[] actual = await _crypto.DecryptDocumentAsync(
                        (string)item["vaultKeyB64"], (string)item["vaultId"], envelope)
                        .ConfigureAwait(false);
                    bool ok = actual != null && actual.Length == expected.Length;
                    if (ok)
                    {
                        for (int i = 0; i < actual.Length; i++)
                        {
                            if (actual[i] != expected[i])
                            {
                                ok = false;
                                break;
                            }
                        }
                    }
                    lines.Add((ok ? "PASS " : "FAIL ") + "桌面向量 " + name
                        + "（明文 " + expected.Length.ToString(CultureInfo.InvariantCulture) + " 字节）");
                    if (!ok)
                    {
                        allOk = false;
                    }
                }
                watch.Stop();
                lines.Add(Format(allOk, "桌面向量共 " + items.Count.ToString(CultureInfo.InvariantCulture) + " 组", watch));
                return allOk;
            }
            catch
            {
                watch.Stop();
                lines.Add(Format(false, "桌面向量解析", watch));
                return false;
            }
        }

        private static string Format(bool ok, string name, Stopwatch watch)
        {
            string line = (ok ? "PASS " : "FAIL ") + name;
            if (watch != null)
            {
                line += " " + watch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + "ms";
            }
            return line;
        }

        private static byte[] HexToBytes(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return bytes;
        }

        private void OnCopyClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_lastReport)) { return; }
            StatusText.Text = DebugReport.CopyToClipboard(_lastReport) ? "已复制到剪贴板" : "复制失败";
        }

        private void OnBackClick(object sender, RoutedEventArgs e)
        {
            if (Frame != null && Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }
    }
}
