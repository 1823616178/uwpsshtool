using Newtonsoft.Json.Linq;

namespace SshTool.Core.Sync.Api.Dtos
{
    // 03-SYNC-PROTOCOL.md §2.2 保险库分组。VaultKeyEnvelope 各字段是 base64 字符串，
    // 客户端不解释内容，只负责透传。

    public sealed class KdfParametersData
    {
        public string Algorithm { get; set; }
        public int Memory { get; set; }
        public int Iterations { get; set; }
        public int Parallelism { get; set; }

        public static KdfParametersData Parse(JObject o, string path)
        {
            return new KdfParametersData
            {
                Algorithm = DtoReader.Str(o, "algorithm", path + ".algorithm"),
                Memory = DtoReader.Int(o, "memory", path + ".memory"),
                Iterations = DtoReader.Int(o, "iterations", path + ".iterations"),
                Parallelism = DtoReader.Int(o, "parallelism", path + ".parallelism")
            };
        }

        public JObject ToJson()
        {
            return new JObject
            {
                ["algorithm"] = Algorithm,
                ["memory"] = Memory,
                ["iterations"] = Iterations,
                ["parallelism"] = Parallelism
            };
        }
    }

    public sealed class VaultKeyEnvelopeData
    {
        public int KeyVersion { get; set; }
        public string PasswordWrappedKey { get; set; }
        public string PasswordWrapNonce { get; set; }
        public string RecoveryWrappedKey { get; set; }
        public string RecoveryWrapNonce { get; set; }
        public string KdfSalt { get; set; }
        public KdfParametersData KdfParameters { get; set; }

        public static VaultKeyEnvelopeData Parse(JObject o, string path)
        {
            return new VaultKeyEnvelopeData
            {
                KeyVersion = DtoReader.Int(o, "keyVersion", path + ".keyVersion"),
                PasswordWrappedKey = DtoReader.Str(o, "passwordWrappedKey", path + ".passwordWrappedKey"),
                PasswordWrapNonce = DtoReader.Str(o, "passwordWrapNonce", path + ".passwordWrapNonce"),
                RecoveryWrappedKey = DtoReader.Str(o, "recoveryWrappedKey", path + ".recoveryWrappedKey"),
                RecoveryWrapNonce = DtoReader.Str(o, "recoveryWrapNonce", path + ".recoveryWrapNonce"),
                KdfSalt = DtoReader.Str(o, "kdfSalt", path + ".kdfSalt"),
                KdfParameters = KdfParametersData.Parse(
                    DtoReader.Obj(o, "kdfParameters", path + ".kdfParameters"), path + ".kdfParameters")
            };
        }

        public JObject ToJson()
        {
            return new JObject
            {
                ["keyVersion"] = KeyVersion,
                ["passwordWrappedKey"] = PasswordWrappedKey,
                ["passwordWrapNonce"] = PasswordWrapNonce,
                ["recoveryWrappedKey"] = RecoveryWrappedKey,
                ["recoveryWrapNonce"] = RecoveryWrapNonce,
                ["kdfSalt"] = KdfSalt,
                ["kdfParameters"] = KdfParameters.ToJson()
            };
        }
    }

    // GET vault/key-envelope 响应：{id, ...VaultKeyEnvelope}
    public sealed class VaultEnvelopeResponse
    {
        public string Id { get; set; }
        public VaultKeyEnvelopeData Envelope { get; set; }

        public static VaultEnvelopeResponse Parse(JObject o)
        {
            return new VaultEnvelopeResponse
            {
                Id = DtoReader.Str(o, "id", "id"),
                Envelope = VaultKeyEnvelopeData.Parse(o, "$")
            };
        }
    }

    // POST vault / PUT vault/key-envelope 响应
    public sealed class VaultWriteResponse
    {
        public string Id { get; set; }
        public int KeyVersion { get; set; }

        public static VaultWriteResponse Parse(JObject o)
        {
            return new VaultWriteResponse
            {
                Id = DtoReader.Str(o, "id", "id"),
                KeyVersion = DtoReader.Int(o, "keyVersion", "keyVersion")
            };
        }
    }

    // POST vault/rotate 响应。revision 是 u64 十进制字符串（§2.1 踩坑 8：不转数字）。
    public sealed class RotateVaultResponse
    {
        public string Id { get; set; }
        public int KeyVersion { get; set; }
        public string Revision { get; set; }
        public string UpdatedAt { get; set; }

        public static RotateVaultResponse Parse(JObject o)
        {
            return new RotateVaultResponse
            {
                Id = DtoReader.Str(o, "id", "id"),
                KeyVersion = DtoReader.Int(o, "keyVersion", "keyVersion"),
                Revision = DtoReader.Str(o, "revision", "revision"),
                UpdatedAt = DtoReader.Str(o, "updatedAt", "updatedAt")
            };
        }
    }
}
