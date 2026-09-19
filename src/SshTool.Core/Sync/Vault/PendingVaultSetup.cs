using System;
using Newtonsoft.Json.Linq;
using SshTool.Core.Sync.Api.Dtos;

namespace SshTool.Core.Sync.Vault
{
    // 03-SYNC-PROTOCOL.md §6.2 pendingVaultSetup：首次创建保险库的本机事务日志。
    // POST vault 响应丢失后，重试/重启必须复用同一份材料 + 同一幂等键（踩坑 #12：先落盘再发请求）。
    // 日志脱敏：ToString/日志中绝不出现 vaultKey 与 recoveryKey。
    public sealed class PendingVaultSetup
    {
        public string IdempotencyKey { get; set; }

        public string VaultKeyBase64 { get; set; }

        public string RecoveryKey { get; set; }

        public VaultKeyEnvelope KeyEnvelope { get; set; }

        // ISO-8601（yyyy-MM-ddTHH:mm:ss.fffZ，UTC），仅用于排查，不参与协议。
        public string CreatedAt { get; set; }

        public PendingVaultSetup Clone()
        {
            return new PendingVaultSetup
            {
                IdempotencyKey = IdempotencyKey,
                VaultKeyBase64 = VaultKeyBase64,
                RecoveryKey = RecoveryKey,
                KeyEnvelope = KeyEnvelope == null ? null : KeyEnvelope.Clone(),
                CreatedAt = CreatedAt
            };
        }

        public JObject ToJson()
        {
            return new JObject
            {
                ["idempotencyKey"] = IdempotencyKey,
                ["vaultKey"] = VaultKeyBase64,
                ["recoveryKey"] = RecoveryKey,
                ["keyEnvelope"] = KeyEnvelope == null ? null : KeyEnvelope.ToJson(),
                ["createdAt"] = CreatedAt
            };
        }

        public static PendingVaultSetup Parse(JObject o)
        {
            if (o == null)
            {
                throw new ProtocolParseException("pendingVaultSetup", "应为对象");
            }
            var pending = new PendingVaultSetup
            {
                IdempotencyKey = DtoReader.Str(o, "idempotencyKey", "pendingVaultSetup.idempotencyKey"),
                VaultKeyBase64 = DtoReader.Str(o, "vaultKey", "pendingVaultSetup.vaultKey"),
                RecoveryKey = DtoReader.Str(o, "recoveryKey", "pendingVaultSetup.recoveryKey"),
                CreatedAt = DtoReader.Str(o, "createdAt", "pendingVaultSetup.createdAt")
            };
            if (string.IsNullOrEmpty(pending.IdempotencyKey)
                || string.IsNullOrEmpty(pending.VaultKeyBase64)
                || string.IsNullOrEmpty(pending.RecoveryKey))
            {
                throw new ProtocolParseException("pendingVaultSetup", "事务字段不能为空");
            }
            pending.KeyEnvelope = VaultKeyEnvelope.Parse(
                DtoReader.Obj(o, "keyEnvelope", "pendingVaultSetup.keyEnvelope"));
            return pending;
        }
    }
}
