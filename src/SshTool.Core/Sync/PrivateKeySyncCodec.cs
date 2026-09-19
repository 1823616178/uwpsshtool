using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SshTool.Core.Common;
using SshTool.Core.Keys;
using SshTool.Core.Sync.Protocol;

namespace SshTool.Core.Sync
{
    // S15 私钥同步编解码（03-SYNC-PROTOCOL.md §4.1 私钥规则、§5.1、§5.2 第 4 条、§9；
    // 桌面端 src/main/sync/sync-serializer.ts 的 encodedPrivateKey /
    // decodeSyncedPrivateKey 的移植，指纹口径与桌面端 publicKeyFingerprint 一致：
    // "SHA256:" + base64_nopad(sha256(公钥 SSH wire blob))）。
    //
    // 分层：PrivateKeySyncCodec 是纯函数（结构校验与组装，不调 keytool，可单测）；
    // KeyToolPrivateKeyInspector 实现 IPrivateKeyInspector，经由 IKeyTool
    //（原生 KeyTool，K01，含指纹）做解析与指纹复算，供 SyncLocalAdapter 注入。
    // 日志只记相位与数量，不记私钥、短语、公钥内容（脱敏）。
    //
    // U17 说明（S15 时 U17 未做）：私钥开关是数据层 preferences.syncPrivateKeys。
    // 开启经 SyncCoordinator.SetPreferencesAsync 直接允许（见 S11，不走轮换）；
    // 关闭必须走 RotateSensitiveSyncAsync（S13，复用轮换流程，旧密钥即刻失效）。
    // U17 的 AccountSyncPage 落地时把“同步私钥”开关绑到这两个入口即可，届时删掉本注。
    public static class PrivateKeySyncCodec
    {
        // PKCS#8 两类 header。桌面端 ssh2@1.17 的 parseKey 对这两类报
        // Unsupported key format（实测，见 tools/sync-vectors/private-keys.mjs 文件头）：
        // 桌面端既不能产生（含此类私钥的文档在桌面端出站即抛），也不能消费
        //（含此类私钥的文档在桌面端入站整份无效）。Lumia 出站若携带此类私钥，
        // 会导致桌面端同步整份报错，故跳过（记警告）；本地登录不受影响
        //（K01/N05 支持 PKCS#8），入站仍接受（Lumia 本地可用）。
        private static readonly Regex Pkcs8Header = new Regex(
            @"^-----BEGIN (?:ENCRYPTED )?PRIVATE KEY-----\r?\n", RegexOptions.Compiled);

        // 出站桌面兼容性：PKCS#8（裸与 ENCRYPTED）返回 false，其余 header 返回 true。
        // text 为空或非 UTF-8 文本时同样返回 false（调用方视为跳过）。
        public static bool IsDesktopCompatible(string privateKeyText)
        {
            if (string.IsNullOrEmpty(privateKeyText))
            {
                return false;
            }
            return !Pkcs8Header.IsMatch(privateKeyText);
        }

        // 出站组装（纯函数；对应桌面端 encodedPrivateKey）：
        // text 非空、UTF-8 字节 ≤ 256 KiB、header 可判定且与 inspected.Format 一致、
        // inspected.Fingerprint 合法、非 PKCS#8 → 返回可同步三元组；
        // 任一条件不满足 → null（调用方跳过该主机私钥段并记警告，不抛，见 §9）。
        // base64 由原文 UTF-8 字节标准编码（Convert 输出恒为规范形式，见 CanonicalBase64）。
        public static InspectedPrivateKey TryEncode(string privateKeyText, InspectedKeyInfo inspected)
        {
            if (string.IsNullOrEmpty(privateKeyText) || inspected == null)
            {
                return null;
            }
            byte[] bytes = Encoding.UTF8.GetBytes(privateKeyText);
            if (bytes.Length == 0 || bytes.Length > SyncConstants.PrivateKeyMaxBytes)
            {
                return null;
            }
            string header = PrivateKeyFormat.Detect(bytes);
            if (header == null || !string.Equals(header, inspected.Format, StringComparison.Ordinal))
            {
                return null;
            }
            if (!PrivateKeyFormat.IsValidFingerprint(inspected.FingerprintSha256))
            {
                // 含加密 PEM 无短语时的部分成功（无指纹，见 IKeyTool）与解析失败。
                return null;
            }
            if (!IsDesktopCompatible(privateKeyText))
            {
                return null;
            }
            return new InspectedPrivateKey
            {
                Base64 = Convert.ToBase64String(bytes),
                Format = header,
                Fingerprint = inspected.FingerprintSha256
            };
        }

        // 入站结构校验（纯函数；对应桌面端 decodeSyncedPrivateKey 的结构部分，
        // 指纹复算由调用方经 IKeyTool 完成）：
        // secrets 为 null 或 PrivateKey 为 null → null（无私钥段；元数据脱离 privateKey
        // 单独出现时抛，见 §4.1 私钥元数据规则）；结构合法 → 私钥原文（UTF-8 文本）；
        // 结构非法 → 抛 SyncApplyException(PrivateKeyInvalid)，整份不应用。
        // 文案与桌面端 decodeSyncedPrivateKey 的中文错误一一对应。
        public static string DecodeStructure(ServerSecrets secrets, string serverId)
        {
            if (serverId == null)
            {
                serverId = "";
            }
            if (secrets == null || secrets.PrivateKey == null)
            {
                if (secrets != null
                    && (secrets.PrivateKeyEncoding != null
                        || secrets.PrivateKeyFormat != null
                        || secrets.PrivateKeyFingerprint != null))
                {
                    throw new SyncApplyException(SyncApplyFailure.PrivateKeyInvalid,
                        "服务器 " + serverId + " 的私钥元数据缺少 privateKey");
                }
                return null;
            }
            if (secrets.PrivateKeyEncoding != PrivateKeyFormat.EncodingBase64
                || string.IsNullOrEmpty(secrets.PrivateKeyFormat)
                || string.IsNullOrEmpty(secrets.PrivateKeyFingerprint))
            {
                throw new SyncApplyException(SyncApplyFailure.PrivateKeyInvalid,
                    "服务器 " + serverId + " 的私钥必须同时包含 encoding、format 和 fingerprint");
            }
            if (!PrivateKeyFormat.IsValidFingerprint(secrets.PrivateKeyFingerprint))
            {
                throw new SyncApplyException(SyncApplyFailure.PrivateKeyInvalid,
                    "服务器 " + serverId + " 的私钥指纹格式无效");
            }
            byte[] decoded = CanonicalBase64.TryDecode(secrets.PrivateKey);
            if (decoded == null)
            {
                throw new SyncApplyException(SyncApplyFailure.PrivateKeyInvalid,
                    "服务器 " + serverId + " 的私钥不是 canonical Base64");
            }
            if (decoded.Length > SyncConstants.PrivateKeyMaxBytes)
            {
                throw new SyncApplyException(SyncApplyFailure.PrivateKeyInvalid,
                    "服务器 " + serverId + " 的私钥超过 256 KiB");
            }
            string header = PrivateKeyFormat.Detect(decoded);
            if (header == null || !string.Equals(header, secrets.PrivateKeyFormat, StringComparison.Ordinal))
            {
                throw new SyncApplyException(SyncApplyFailure.PrivateKeyInvalid,
                    "服务器 " + serverId + " 的私钥 header 与声明格式不一致");
            }
            // Detect 已用严格 UTF-8 解码验证，此处不会抛。
            return Encoding.UTF8.GetString(decoded, 0, decoded.Length);
        }

        // 加密启发式（与 K02 KeyImportService.LooksEncrypted 同规则）：
        // 传统 PEM 的 Proc-Type/DEK-Info 标记或 ENCRYPTED 字样。
        // 用于“无短语且 IKeyTool 无法解析”时区分“缺短语的加密钥（容忍）”与
        // “损坏文件（拒绝）”，对应桌面端对 ssh2 错误消息的 /encrypt|passphrase/i 判断。
        public static bool LooksEncrypted(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            return text.IndexOf("ENCRYPTED", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("Proc-Type: 4,ENCRYPTED", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("DEK-Info:", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    // IKeyTool 支撑的检查器（K01 原生 KeyTool 含指纹；单测注入 FakeKeyTool）。
    public sealed class KeyToolPrivateKeyInspector : IPrivateKeyInspector
    {
        private const string Tag = "PrivateKeySync";

        private readonly IKeyTool _tool;
        private readonly ILogger _log;

        public KeyToolPrivateKeyInspector(IKeyTool tool, ILogger logger = null)
        {
            if (tool == null)
            {
                throw new ArgumentNullException("tool");
            }
            _tool = tool;
            _log = logger;
        }

        // 出站（§9）：任一环节失败 → null（调用方跳过该主机私钥段并记警告）。
        // 短语由调用方（SyncLocalAdapter，见 §5.1）按 key: 优先、host: 其次取好传入。
        public async Task<InspectedPrivateKey> InspectAsync(string privateKeyText, string passphrase)
        {
            if (string.IsNullOrEmpty(privateKeyText))
            {
                return null;
            }
            int byteCount = Encoding.UTF8.GetByteCount(privateKeyText);
            if (byteCount > SyncConstants.PrivateKeyMaxBytes)
            {
                Warn("出站跳过：超过 256 KiB");
                return null;
            }
            if (!PrivateKeySyncCodec.IsDesktopCompatible(privateKeyText))
            {
                Warn("出站跳过：PKCS#8 桌面端不兼容");
                return null;
            }
            InspectedKeyInfo info = await _tool.InspectAsync(
                privateKeyText, passphrase ?? string.Empty).ConfigureAwait(false);
            InspectedPrivateKey encoded = PrivateKeySyncCodec.TryEncode(privateKeyText, info);
            if (encoded == null)
            {
                Warn("出站跳过：无法解析或无指纹");
            }
            return encoded;
        }

        // 入站（§4.1 末段 + §9）：结构失败 → SyncApplyException；
        // 有短语或未加密（能解析出指纹）时复算指纹，不等 → SyncApplyException；
        // 加密私钥且无短语 → 只做 header/元数据校验（DecodeStructure 已做），接受。
        //
        // 与桌面端的一处 fail-closed 差异：openssh-key-v1 加密钥无短语时，
        // 桌面端 ssh2 报缺短语错误即跳过指纹复算，而原生 KeyTool 可直接从文件头
        // 取公钥（§9），此处仍复算。损坏/伪造的指纹在桌面端会被接受、在此处被拒；
        // 合法文档两端都接受（桌面端出站恒写正确指纹），故不影响互通。
        public async Task<string> DecodeAndVerifyAsync(ServerSecrets secrets, string serverId)
        {
            string text = PrivateKeySyncCodec.DecodeStructure(secrets, serverId);
            if (text == null)
            {
                return null;
            }
            InspectedKeyInfo info = await _tool.InspectAsync(
                text, secrets.Passphrase ?? string.Empty).ConfigureAwait(false);
            if (info != null && !string.IsNullOrEmpty(info.FingerprintSha256))
            {
                if (!string.Equals(info.FingerprintSha256, secrets.PrivateKeyFingerprint,
                    StringComparison.Ordinal))
                {
                    throw new SyncApplyException(SyncApplyFailure.PrivateKeyInvalid,
                        "服务器 " + (serverId ?? "") + " 的私钥公钥指纹不匹配");
                }
                return text;
            }
            if (info != null && info.Encrypted)
            {
                // 加密 PEM 无短语时的部分成功：无指纹可复算，接受（§4.1）。
                return text;
            }
            if (info == null && secrets.Passphrase == null && PrivateKeySyncCodec.LooksEncrypted(text))
            {
                // 短语缺失的加密钥：对应桌面端 missingPassphrase 容忍分支，接受。
                return text;
            }
            throw new SyncApplyException(SyncApplyFailure.PrivateKeyInvalid,
                "服务器 " + (serverId ?? "") + " 的私钥无法使用同步的密码短语解析");
        }

        // 只记相位（跳过/失败），不记私钥、短语、公钥内容。
        private void Warn(string message)
        {
            ILogger log = _log;
            if (log != null)
            {
                log.Log(LogLevel.Warning, Tag, message);
            }
        }
    }
}
