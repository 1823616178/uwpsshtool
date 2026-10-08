using System;
using SshTool.Core.Models;

namespace SshTool.Core.Sessions
{
    public enum HostKeyVerdictKind
    {
        Accept = 0,
        RejectMismatch = 1,
        PromptUnknown = 2,
        // 无交互场景（后台隧道）遇到未知主机：既无 known_hosts 也无钉住指纹 → 拒绝，
        // 只由 VerifyNonInteractive 返回；用户须先在前台终端连接一次并确认信任。
        RejectUnknown = 3
    }

    public sealed class HostKeyVerdict
    {
        public HostKeyVerdictKind Kind { get; set; }
        public bool WriteKnownHost { get; set; }
    }

    // 01-DESIGN.md §9.2：KnownHost 优先，其次 Host.hostFingerprint，否则 TOFU 弹框。
    //
    // opt/full-pass：hostFingerprint 是随同步跨设备漫游的「钉住」指纹，而 known_hosts 只在
    // 本机。新设备上 known_hosts 为空时，若钉住指纹与对端不一致，必须按**不匹配**拒绝
    // （303 + HostKeyMismatchDialog），不能退化成看起来和首次连接一样的 TOFU 弹框——
    // 否则中间人只需用户点一次「信任」就会被写进 known_hosts（后台隧道 NativeForwarder
    // 走 TOFU 静默接受，旧逻辑下甚至无需任何交互）。恢复路径：主机编辑页「重置指纹」。
    public static class HostKeyVerifier
    {
        public static HostKeyVerdict Verify(KnownHost known, string hostFingerprint, HostKeyInfo presented)
        {
            if (presented == null || string.IsNullOrEmpty(presented.FingerprintSha256))
            {
                return new HostKeyVerdict { Kind = HostKeyVerdictKind.RejectMismatch };
            }
            if (known != null)
            {
                if (string.Equals(known.FingerprintSha256, presented.FingerprintSha256, StringComparison.Ordinal))
                {
                    return new HostKeyVerdict { Kind = HostKeyVerdictKind.Accept };
                }
                return new HostKeyVerdict { Kind = HostKeyVerdictKind.RejectMismatch };
            }
            if (!string.IsNullOrEmpty(hostFingerprint))
            {
                if (string.Equals(hostFingerprint, presented.FingerprintSha256, StringComparison.Ordinal))
                {
                    return new HostKeyVerdict { Kind = HostKeyVerdictKind.Accept, WriteKnownHost = true };
                }
                return new HostKeyVerdict { Kind = HostKeyVerdictKind.RejectMismatch };
            }
            return new HostKeyVerdict { Kind = HostKeyVerdictKind.PromptUnknown, WriteKnownHost = true };
        }

        // 评审（PR #1）：没有用户可交互的调用方（后台隧道 NativeForwarder）用这个入口。
        // 与 Verify 相同，只是把 PromptUnknown 换成 RejectUnknown（不接受、不写 known_hosts），
        // 杜绝首次隧道连接静默信任中间人。已知主机 / 钉住指纹一致时照常接受。
        public static HostKeyVerdict VerifyNonInteractive(KnownHost known, string hostFingerprint, HostKeyInfo presented)
        {
            HostKeyVerdict verdict = Verify(known, hostFingerprint, presented);
            if (verdict.Kind == HostKeyVerdictKind.PromptUnknown)
            {
                return new HostKeyVerdict { Kind = HostKeyVerdictKind.RejectUnknown, WriteKnownHost = false };
            }
            return verdict;
        }
    }
}
