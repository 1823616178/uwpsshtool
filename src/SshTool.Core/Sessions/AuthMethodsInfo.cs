using System;
using SshTool.Core.Common;
using SshTool.Core.Models;

namespace SshTool.Core.Sessions
{
    // fix/functional-pass：服务器声明的认证方式（native queryAuthMethods → libssh2_userauth_list）。
    // 用途：keyboard-interactive（2FA/OTP/PAM）的选择——
    //   1. 主方式（密码 / 公钥）不在列表、但列表有 keyboard-interactive → 直接走 KI；
    //   2. 主方式「失败」后重查：列表里已没有刚用过的方式、却有 keyboard-interactive
    //      → 视为部分成功（AuthenticationMethods publickey,keyboard-interactive 之类），继续 KI。
    // native 侧密码/公钥失败从不返回 203，旧代码按 203 回退 KI 的分支永远走不到。
    public sealed class AuthMethodsInfo
    {
        public const string AuthenticatedMarker = "+authenticated";

        public static readonly AuthMethodsInfo Unknown = new AuthMethodsInfo(null);

        private AuthMethodsInfo(string raw)
        {
            Raw = raw ?? string.Empty;
            if (string.IsNullOrEmpty(raw))
            {
                return;
            }
            if (string.Equals(raw, AuthenticatedMarker, StringComparison.Ordinal))
            {
                Known = true;
                Authenticated = true;
                return;
            }
            Known = true;
            string[] items = raw.Split(',');
            for (int i = 0; i < items.Length; i++)
            {
                string item = items[i].Trim();
                if (string.Equals(item, "password", StringComparison.OrdinalIgnoreCase))
                {
                    Password = true;
                }
                else if (string.Equals(item, "publickey", StringComparison.OrdinalIgnoreCase))
                {
                    PublicKey = true;
                }
                else if (string.Equals(item, "keyboard-interactive", StringComparison.OrdinalIgnoreCase))
                {
                    KeyboardInteractive = true;
                }
            }
        }

        public static AuthMethodsInfo Parse(string raw)
        {
            return string.IsNullOrEmpty(raw) ? Unknown : new AuthMethodsInfo(raw);
        }

        // 探测是否成功（失败/未受理 = 不知道，按旧流程走）。
        public bool Known { get; private set; }
        // 服务器接受了 "none"：会话已 Established，不必再认证。
        public bool Authenticated { get; private set; }
        public bool Password { get; private set; }
        public bool PublicKey { get; private set; }
        public bool KeyboardInteractive { get; private set; }
        public string Raw { get; private set; }

        public bool Offers(AuthType type)
        {
            return type == AuthType.Password ? Password : PublicKey;
        }

        // 认证前：主方式不被提供、KI 被提供 → 直接 KI。
        public static bool ShouldStartWithKeyboardInteractive(AuthMethodsInfo methods, AuthType type)
        {
            return methods != null && methods.Known && !methods.Authenticated
                && methods.KeyboardInteractive && !methods.Offers(type);
        }

        // 主方式被拒后重查的结果：刚用过的方式已不在列表、KI 在 → 部分成功后继续 KI。
        public static bool ShouldContinueWithKeyboardInteractive(AuthMethodsInfo after, AuthType type)
        {
            return ShouldStartWithKeyboardInteractive(after, type);
        }

        // 值得重查方法列表的失败码（服务器拒绝该凭据；本地取钥失败/超时等不算）。
        public static bool IsServerRejection(SshErrorCode code)
        {
            return code == SshErrorCode.AuthPasswordFailed || code == SshErrorCode.AuthPublicKeyFailed;
        }
    }
}
