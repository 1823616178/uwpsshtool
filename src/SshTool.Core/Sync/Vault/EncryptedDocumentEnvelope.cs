using Newtonsoft.Json.Linq;
using SshTool.Core.Sync.Api.Dtos;
using SshTool.Core.Sync.Protocol;

namespace SshTool.Core.Sync.Vault
{
    // S05：加密文档信封的 Core 域模型（03-SYNC-PROTOCOL.md §3.4）。
    //
    // 与桌面端 EncryptedDocumentEnvelope（sync-types.ts）逐键对应；即 PUT
    // sync/document 的请求体、GET sync/document 响应中的密文部分（S11 使用）。
    // algorithm 必须为 "AES-256-GCM"，否则 Parse 直接拒绝（与桌面端
    // decryptSyncDocument 前置条件一致）。未知键忽略（服务端兼容）。
    public sealed class EncryptedDocumentEnvelope
    {
        public int SchemaVersion { get; set; }
        public int KeyVersion { get; set; }
        public string Algorithm { get; set; }
        public string Nonce { get; set; }
        public string Ciphertext { get; set; }
        public string CiphertextHash { get; set; }

        public EncryptedDocumentEnvelope Clone()
        {
            return new EncryptedDocumentEnvelope
            {
                SchemaVersion = SchemaVersion,
                KeyVersion = KeyVersion,
                Algorithm = Algorithm,
                Nonce = Nonce,
                Ciphertext = Ciphertext,
                CiphertextHash = CiphertextHash
            };
        }

        public JObject ToJson()
        {
            return new JObject
            {
                ["schemaVersion"] = SchemaVersion,
                ["keyVersion"] = KeyVersion,
                ["algorithm"] = Algorithm,
                ["nonce"] = Nonce,
                ["ciphertext"] = Ciphertext,
                ["ciphertextHash"] = CiphertextHash
            };
        }

        public static EncryptedDocumentEnvelope Parse(JObject o)
        {
            if (o == null)
            {
                throw new ProtocolParseException("$", "信封不是对象");
            }
            var envelope = new EncryptedDocumentEnvelope
            {
                SchemaVersion = DtoReader.Int(o, "schemaVersion", "schemaVersion"),
                KeyVersion = DtoReader.Int(o, "keyVersion", "keyVersion"),
                Algorithm = DtoReader.Str(o, "algorithm", "algorithm"),
                Nonce = DtoReader.Str(o, "nonce", "nonce"),
                Ciphertext = DtoReader.Str(o, "ciphertext", "ciphertext"),
                CiphertextHash = DtoReader.Str(o, "ciphertextHash", "ciphertextHash")
            };
            if (envelope.SchemaVersion < 1)
            {
                throw new ProtocolParseException("schemaVersion", "必须 ≥1");
            }
            if (envelope.KeyVersion < 1)
            {
                throw new ProtocolParseException("keyVersion", "必须 ≥1");
            }
            if (envelope.Algorithm != SyncConstants.AesAlgorithm)
            {
                throw new ProtocolParseException("algorithm", "仅支持 AES-256-GCM");
            }
            return envelope;
        }
    }
}
