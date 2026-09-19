using System;
using Newtonsoft.Json.Linq;
using SshTool.Core.Sync.Api.Dtos;
using SshTool.Core.Sync.Protocol;

namespace SshTool.Core.Sync.Vault
{
    // S05：保险库信封的 Core 域模型（03-SYNC-PROTOCOL.md §3.3 envelope）。
    //
    // 与桌面端 VaultKeyEnvelope（sync-types.ts）逐键对应；JSON 键名保持 camelCase，
    // 供服务端透传（S11 的 SetupVault/Unlock）与本地 pendingVaultSetup 落盘使用。
    // KDF 参数一律来自信封并按 SyncConstants 范围校验（踩坑 #1：禁止写死常量）。
    // 解析容忍未知键（服务端可能新增字段，见 ProtocolParseException 注释），
    // 但必需键缺失/类型不符/KDF 越界一律抛 ProtocolParseException。
    public sealed class VaultKeyEnvelope
    {
        public int KeyVersion { get; set; }
        public string PasswordWrappedKey { get; set; }
        public string PasswordWrapNonce { get; set; }
        public string RecoveryWrappedKey { get; set; }
        public string RecoveryWrapNonce { get; set; }
        public string KdfSalt { get; set; }
        public string KdfAlgorithm { get; set; }
        public int KdfMemory { get; set; }
        public int KdfIterations { get; set; }
        public int KdfParallelism { get; set; }

        public VaultKeyEnvelope Clone()
        {
            return new VaultKeyEnvelope
            {
                KeyVersion = KeyVersion,
                PasswordWrappedKey = PasswordWrappedKey,
                PasswordWrapNonce = PasswordWrapNonce,
                RecoveryWrappedKey = RecoveryWrappedKey,
                RecoveryWrapNonce = RecoveryWrapNonce,
                KdfSalt = KdfSalt,
                KdfAlgorithm = KdfAlgorithm,
                KdfMemory = KdfMemory,
                KdfIterations = KdfIterations,
                KdfParallelism = KdfParallelism
            };
        }

        // KDF 参数范围校验（§3.1，解锁时校验信封用）。
        public static bool IsValidKdf(string algorithm, int memory, int iterations, int parallelism)
        {
            if (!string.Equals(algorithm, SyncConstants.KdfAlgorithm, StringComparison.Ordinal))
            {
                return false;
            }
            return memory >= SyncConstants.KdfMemoryKibMin && memory <= SyncConstants.KdfMemoryKibMax
                && iterations >= SyncConstants.KdfIterationsMin && iterations <= SyncConstants.KdfIterationsMax
                && parallelism >= SyncConstants.KdfParallelismMin && parallelism <= SyncConstants.KdfParallelismMax;
        }

        public bool HasValidKdf()
        {
            return IsValidKdf(KdfAlgorithm, KdfMemory, KdfIterations, KdfParallelism);
        }

        // 固定键序写出（§4.2 风格：键序稳定，便于 pending 落盘后比对）。
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
                ["kdfParameters"] = new JObject
                {
                    ["algorithm"] = KdfAlgorithm,
                    ["memory"] = KdfMemory,
                    ["iterations"] = KdfIterations,
                    ["parallelism"] = KdfParallelism
                }
            };
        }

        public static VaultKeyEnvelope Parse(JObject o)
        {
            if (o == null)
            {
                throw new ProtocolParseException("$", "信封不是对象");
            }
            var envelope = new VaultKeyEnvelope
            {
                KeyVersion = DtoReader.Int(o, "keyVersion", "keyVersion"),
                PasswordWrappedKey = DtoReader.Str(o, "passwordWrappedKey", "passwordWrappedKey"),
                PasswordWrapNonce = DtoReader.Str(o, "passwordWrapNonce", "passwordWrapNonce"),
                RecoveryWrappedKey = DtoReader.Str(o, "recoveryWrappedKey", "recoveryWrappedKey"),
                RecoveryWrapNonce = DtoReader.Str(o, "recoveryWrapNonce", "recoveryWrapNonce"),
                KdfSalt = DtoReader.Str(o, "kdfSalt", "kdfSalt")
            };
            if (envelope.KeyVersion < 1)
            {
                throw new ProtocolParseException("keyVersion", "必须 ≥1");
            }
            JObject kdf = DtoReader.Obj(o, "kdfParameters", "kdfParameters");
            envelope.KdfAlgorithm = DtoReader.Str(kdf, "algorithm", "kdfParameters.algorithm");
            envelope.KdfMemory = DtoReader.Int(kdf, "memory", "kdfParameters.memory");
            envelope.KdfIterations = DtoReader.Int(kdf, "iterations", "kdfParameters.iterations");
            envelope.KdfParallelism = DtoReader.Int(kdf, "parallelism", "kdfParameters.parallelism");
            if (!envelope.HasValidKdf())
            {
                throw new ProtocolParseException("kdfParameters", "Argon2id 参数越界");
            }
            return envelope;
        }
    }
}
