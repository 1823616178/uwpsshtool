using System;
using SshTool.Core.Models;

namespace SshTool.Core.Sessions
{
    public enum HostKeyVerdictKind
    {
        Accept = 0,
        RejectMismatch = 1,
        PromptUnknown = 2
    }

    public sealed class HostKeyVerdict
    {
        public HostKeyVerdictKind Kind { get; set; }
        public bool WriteKnownHost { get; set; }
    }

    // 01-DESIGN.md §9.2：KnownHost 优先，其次 Host.hostFingerprint，否则 TOFU 弹框。
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
            if (!string.IsNullOrEmpty(hostFingerprint)
                && string.Equals(hostFingerprint, presented.FingerprintSha256, StringComparison.Ordinal))
            {
                return new HostKeyVerdict { Kind = HostKeyVerdictKind.Accept, WriteKnownHost = true };
            }
            return new HostKeyVerdict { Kind = HostKeyVerdictKind.PromptUnknown, WriteKnownHost = true };
        }
    }
}
