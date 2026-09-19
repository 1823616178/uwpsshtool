using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using SshTool.Core.Common;
using SshTool.Core.Keys;
using NativeBridge = SshTool.Native.Bridge;

namespace SshTool.App.Platform
{
    // K02：IKeyTool 的原生适配（01-DESIGN.md §4.1：Core 不引用 Native，
    // WinRT 类型转换与 IAsyncOperation→Task 都在本层；模式见 NativeVaultCrypto）。
    //
    // 线程：native 方法经 create_async 已在后台线程执行，本层只做转换，
    // 全程 ConfigureAwait(false)，绝不回 UI 线程。
    // 失败语义：native 返回 null → 本层返回 null；WinRT 异常同样转 null
    // （记类型名，不记消息与参数）。
    // 日志脱敏：只记方法名 + 成功与否 + 耗时毫秒；绝不记私钥、短语、公钥内容。
    public sealed class NativeKeyTool : IKeyTool
    {
        private readonly ILogger _logger;

        public NativeKeyTool(ILogger logger)
        {
            _logger = logger;
        }

        public NativeKeyTool()
            : this(null)
        {
        }

        public async Task<InspectedKeyInfo> InspectAsync(string privateKeyText, string passphrase)
        {
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                if (string.IsNullOrEmpty(privateKeyText))
                {
                    Log("InspectAsync", "失败", watch);
                    return null;
                }
                byte[] bytes = Encoding.UTF8.GetBytes(privateKeyText);
                NativeBridge.KeyInfo info = await NativeBridge.KeyTool.InspectAsync(
                    bytes, passphrase ?? string.Empty).AsTask().ConfigureAwait(false);
                if (info == null)
                {
                    Log("InspectAsync", "失败", watch);
                    return null;
                }
                Log("InspectAsync", "成功", watch);
                return FromNative(info);
            }
            catch (Exception ex)
            {
                Log("InspectAsync", "异常 " + ex.GetType().Name, watch);
                return null;
            }
        }

        public async Task<string> GenerateEd25519Async(string comment)
        {
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                Windows.Storage.Streams.IBuffer buffer = await NativeBridge.KeyTool
                    .GenerateEd25519Async(comment ?? string.Empty).AsTask().ConfigureAwait(false);
                string text = BufferToString(buffer);
                Log("GenerateEd25519Async", string.IsNullOrEmpty(text) ? "失败" : "成功", watch);
                return string.IsNullOrEmpty(text) ? null : text;
            }
            catch (Exception ex)
            {
                Log("GenerateEd25519Async", "异常 " + ex.GetType().Name, watch);
                return null;
            }
        }

        public async Task<string> GenerateRsaAsync(int bits, string comment)
        {
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                if (bits != 3072 && bits != 4096)
                {
                    Log("GenerateRsaAsync", "失败", watch);
                    return null;
                }
                Windows.Storage.Streams.IBuffer buffer = await NativeBridge.KeyTool
                    .GenerateRsaAsync(bits, comment ?? string.Empty).AsTask().ConfigureAwait(false);
                string text = BufferToString(buffer);
                Log("GenerateRsaAsync", string.IsNullOrEmpty(text) ? "失败" : "成功", watch);
                return string.IsNullOrEmpty(text) ? null : text;
            }
            catch (Exception ex)
            {
                Log("GenerateRsaAsync", "异常 " + ex.GetType().Name, watch);
                return null;
            }
        }

        internal static InspectedKeyInfo FromNative(NativeBridge.KeyInfo native)
        {
            return new InspectedKeyInfo
            {
                KeyType = native.KeyType ?? string.Empty,
                Bits = native.Bits,
                Format = native.Format ?? string.Empty,
                Encrypted = native.Encrypted,
                PublicKeyOpenSsh = native.PublicKeyOpenSsh ?? string.Empty,
                FingerprintSha256 = native.FingerprintSha256 ?? string.Empty,
                Comment = native.Comment ?? string.Empty
            };
        }

        private static string BufferToString(Windows.Storage.Streams.IBuffer buffer)
        {
            if (buffer == null || buffer.Length == 0 || buffer.Length > int.MaxValue)
            {
                return null;
            }
            byte[] bytes;
            Windows.Security.Cryptography.CryptographicBuffer.CopyToByteArray(buffer, out bytes);
            if (bytes == null || bytes.Length == 0)
            {
                return null;
            }
            return Encoding.UTF8.GetString(bytes, 0, bytes.Length);
        }

        // 只记相位与耗时：LogRedactor 会二次兜底，但本层消息本来就不含敏感材料。
        private void Log(string method, string outcome, Stopwatch watch)
        {
            ILogger logger = _logger;
            if (logger == null)
            {
                return;
            }
            watch.Stop();
            logger.Log(LogLevel.Debug, "KeyTool",
                method + " " + outcome + " " + watch.ElapsedMilliseconds + "ms");
        }
    }
}
