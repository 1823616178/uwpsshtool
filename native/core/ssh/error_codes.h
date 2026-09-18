#pragma once

// N08: unified SSH error codes (01-DESIGN.md section 6.3).
//
// The single source of truth is the C# SshErrorCode enum in
// src/SshTool.Core/Common/SshErrorCode.cs (the user-facing message table is
// anchored to it); this header maps native SshSessionError values to codes
// numerically identical to it. Only the numeric code crosses the layer
// boundary (Native -> Core); display resolves messages from the code.
//
// Number ranges: 1xx connect & network, 2xx auth, 3xx negotiation & host key,
// 4xx session, 5xx internal, 999 fallback. 0 is the reserved "no error"
// value. Constant names mirror the C# member names one-to-one
// (kSshErrorCode<CsMemberName>) so the contract check compares name=value
// pairs item by item.
//
// Drift guards (the three places reference each other; changing one requires
// syncing the others):
//   1. this header's kSshErrorCode* table (native-side list);
//   2. SshErrorCode.cs (C#-side list);
//   3. scripts/check-error-codes.ps1: verify.ps1 runs the contract check;
//      any mismatch fails the gate.
// Mapping exhaustiveness is asserted by tests/error_codes_test.cpp: no
// SshSessionError value may fall to kSshErrorCodeUnknown (adding an enum
// value requires a constant here and a switch branch below).
//
// Pure logic (header-only): depends only on session.h's enum; no WinRT
// includes.

#include <libssh2.h>

#include "session.h"

namespace sshclient {
namespace ssh {

// ---- unified error code constants (numerically identical to the C#
// SshErrorCode enum; comments carry the C#-side Chinese message gist) ----

inline constexpr int kSshErrorCodeNone = 0; // 无错误（保留值，不投递）

// 1xx 连接与网络
inline constexpr int kSshErrorCodeDnsResolutionFailed = 101; // 无法解析主机名
inline constexpr int kSshErrorCodeConnectTimeout = 102;      // 连接超时
inline constexpr int kSshErrorCodeConnectionRefused = 103;   // 连接被拒绝
inline constexpr int kSshErrorCodeNetworkUnreachable = 104;  // 网络不可达

// 2xx 认证
inline constexpr int kSshErrorCodeAuthPasswordFailed = 201;            // 密码认证失败
inline constexpr int kSshErrorCodeAuthPublicKeyFailed = 202;           // 公钥认证失败
inline constexpr int kSshErrorCodeAuthKeyboardInteractiveFailed = 203; // 交互式认证失败
inline constexpr int kSshErrorCodePrivateKeyLoadFailed = 204;          // 私钥短语错误/无法加载
inline constexpr int kSshErrorCodeAuthTimeout = 205;                   // 认证超时
inline constexpr int kSshErrorCodeNoLocalCredential = 206;             // 无可用本地凭据（Core 产出）

// 3xx 协商与主机密钥
inline constexpr int kSshErrorCodeAlgorithmNegotiationFailed = 301; // 算法协商失败
inline constexpr int kSshErrorCodeUnknownHostKey = 302;  // 主机密钥未知（TOFU 待确认；
                                                         // native 不产出，Core 编排）
inline constexpr int kSshErrorCodeHostKeyMismatch = 303; // 主机密钥与本地记录不匹配
inline constexpr int kSshErrorCodeHandshakeFailed = 304; // SSH 握手失败（非算法类）
inline constexpr int kSshErrorCodeHandshakeTimeout = 305; // SSH 握手超时

// 4xx 会话
inline constexpr int kSshErrorCodeRemoteClosed = 401;      // 连接被远端关闭
inline constexpr int kSshErrorCodeSessionTimeout = 402;    // 会话超时（通用；native 暂不产出，
                                                           // 细分码优先）
inline constexpr int kSshErrorCodeSocketError = 403;       // 底层 socket 错误
inline constexpr int kSshErrorCodeKeepaliveTimeout = 404;  // keepalive 静默黑洞
inline constexpr int kSshErrorCodePolicyDisconnect = 405;  // 策略断开（Core 编排产出）

// 5xx 内部
inline constexpr int kSshErrorCodeInternalError = 500; // 内部错误（资源创建失败等）

// 999 兜底（映射函数只在收到未知枚举强转/未知 libssh2 码时产出）
inline constexpr int kSshErrorCodeUnknown = 999;

// SshSessionError -> unified numeric code (None -> 0). The switch covers every
// enum value and deliberately has no default: a new enum value makes MSVC
// C4061 name it, and error_codes_test.cpp's exhaustiveness assertion keeps
// any value from landing on kSshErrorCodeUnknown.
inline int toSshErrorCode(SshSessionError error)
{
    switch (error) {
    case SshSessionError::None:                       return kSshErrorCodeNone;
    case SshSessionError::DnsResolutionFailed:        return kSshErrorCodeDnsResolutionFailed;
    case SshSessionError::ConnectTimeout:             return kSshErrorCodeConnectTimeout;
    case SshSessionError::ConnectionRefused:          return kSshErrorCodeConnectionRefused;
    case SshSessionError::NetworkUnreachable:         return kSshErrorCodeNetworkUnreachable;
    case SshSessionError::AuthFailedPassword:         return kSshErrorCodeAuthPasswordFailed;
    case SshSessionError::AuthFailedKey:              return kSshErrorCodeAuthPublicKeyFailed;
    case SshSessionError::AuthFailedInteractive:      return kSshErrorCodeAuthKeyboardInteractiveFailed;
    case SshSessionError::AuthFailedPassphrase:       return kSshErrorCodePrivateKeyLoadFailed;
    case SshSessionError::AuthTimeout:                return kSshErrorCodeAuthTimeout;
    case SshSessionError::NoLocalCredential:          return kSshErrorCodeNoLocalCredential;
    case SshSessionError::AlgorithmNegotiationFailed: return kSshErrorCodeAlgorithmNegotiationFailed;
    case SshSessionError::HostKeyMismatch:            return kSshErrorCodeHostKeyMismatch;
    case SshSessionError::HandshakeFailed:            return kSshErrorCodeHandshakeFailed;
    case SshSessionError::HandshakeTimeout:           return kSshErrorCodeHandshakeTimeout;
    case SshSessionError::RemoteClosed:               return kSshErrorCodeRemoteClosed;
    case SshSessionError::SocketError:                return kSshErrorCodeSocketError;
    case SshSessionError::KeepaliveTimeout:           return kSshErrorCodeKeepaliveTimeout;
    case SshSessionError::InternalError:              return kSshErrorCodeInternalError;
    }
    return kSshErrorCodeUnknown; // unreachable (switch is exhaustive); defense
                                 // against unknown enum casts
}

// libssh2 error code -> unified code: the GENERIC fallback for diagnostics.
// Context-aware paths (connect / handshake / auth) map to finer codes
// themselves (session.cpp / auth.cpp); this covers the common libssh2 errors
// and lands anything unrecognized on kSshErrorCodeUnknown.
inline int toSshErrorCodeFromLibssh2(int libssh2Error)
{
    switch (libssh2Error) {
    // timeout
    case LIBSSH2_ERROR_SOCKET_TIMEOUT:
        return kSshErrorCodeSessionTimeout;
    // auth
    case LIBSSH2_ERROR_PASSWORD_EXPIRED:
    case LIBSSH2_ERROR_AUTHENTICATION_FAILED:
        return kSshErrorCodeAuthPasswordFailed; // generic auth rejection;
                                                // method-specific paths refine
    case LIBSSH2_ERROR_PUBLICKEY_UNVERIFIED:
        return kSshErrorCodeAuthPublicKeyFailed;
    case LIBSSH2_ERROR_KEYFILE_AUTH_FAILED:
        return kSshErrorCodePrivateKeyLoadFailed;
    // algorithm negotiation
    case LIBSSH2_ERROR_KEX_FAILURE:
    case LIBSSH2_ERROR_KEY_EXCHANGE_FAILURE:
    case LIBSSH2_ERROR_METHOD_NOT_SUPPORTED:
    case LIBSSH2_ERROR_ALGO_UNSUPPORTED:
        return kSshErrorCodeAlgorithmNegotiationFailed;
    // host key
    case LIBSSH2_ERROR_HOSTKEY_INIT:
    case LIBSSH2_ERROR_HOSTKEY_SIGN:
    case LIBSSH2_ERROR_KNOWN_HOSTS:
        return kSshErrorCodeHostKeyMismatch;
    // socket
    case LIBSSH2_ERROR_SOCKET_DISCONNECT:
        return kSshErrorCodeRemoteClosed;
    case LIBSSH2_ERROR_SOCKET_SEND:
    case LIBSSH2_ERROR_SOCKET_RECV:
        return kSshErrorCodeSocketError;
    default:
        return kSshErrorCodeUnknown;
    }
}

} // namespace ssh
} // namespace sshclient
