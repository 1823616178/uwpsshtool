// N08 unified error code tests — ssh/error_codes.h.
//
// Covers:
//   - exhaustiveness: every SshSessionError value maps to something other
//     than kSshErrorCodeUnknown — a new enum value without a toSshErrorCode
//     branch turns this red immediately (kAllSessionErrors must be extended
//     too, and the switch has no default so MSVC C4061 names it as well);
//   - numeric anchoring: each enum value maps to the exact number of the C#
//     SshErrorCode enum in src/SshTool.Core/Common/SshErrorCode.cs. The two
//     lists are contract-checked by scripts/check-error-codes.ps1 in
//     verify.ps1 (changing one side without the other fails the gate);
//   - the libssh2 -> code fallback mapping: timeout / auth / host key /
//     socket / negotiation classes plus the unknown -> 999 fallback;
//   - toString(SshSessionError): every enum value has a real name.
#include "ssh/session.h" // winsock2.h must precede windows.h/gtest.

#include <gtest/gtest.h>

#include <libssh2.h>

#include "ssh/error_codes.h"

using sshclient::ssh::SshSessionError;
using sshclient::ssh::toSshErrorCode;
using sshclient::ssh::toSshErrorCodeFromLibssh2;
using sshclient::ssh::toString;
namespace codes = sshclient::ssh;

namespace {

// Every SshSessionError value (mirrors ssh/session.h; a new enum value must
// be appended here or the exhaustiveness assertion cannot see it).
constexpr SshSessionError kAllSessionErrors[] = {
    SshSessionError::None,
    SshSessionError::DnsResolutionFailed,
    SshSessionError::ConnectTimeout,
    SshSessionError::ConnectionRefused,
    SshSessionError::NetworkUnreachable,
    SshSessionError::AuthFailedPassword,
    SshSessionError::AuthFailedKey,
    SshSessionError::AuthFailedInteractive,
    SshSessionError::AuthFailedPassphrase,
    SshSessionError::AuthTimeout,
    SshSessionError::NoLocalCredential,
    SshSessionError::AlgorithmNegotiationFailed,
    SshSessionError::HostKeyMismatch,
    SshSessionError::HandshakeFailed,
    SshSessionError::HandshakeTimeout,
    SshSessionError::RemoteClosed,
    SshSessionError::SocketError,
    SshSessionError::KeepaliveTimeout,
    SshSessionError::InternalError,
};

// ================================================================== exhaustiveness

TEST(SshErrorCodeTest, MappingIsExhaustiveNeverUnknown)
{
    for (const SshSessionError error : kAllSessionErrors) {
        const int code = toSshErrorCode(error);
        EXPECT_NE(code, codes::kSshErrorCodeUnknown)
            << "SshSessionError::" << toString(error) << " mapped to the UNKNOWN fallback — "
            << "add a toSshErrorCode branch and the constant in error_codes.h";
        if (error == SshSessionError::None) {
            EXPECT_EQ(code, codes::kSshErrorCodeNone); // no error = reserved 0
        } else {
            EXPECT_GT(code, 0) << toString(error) << " must not map to the reserved 0";
        }
    }
}

// ================================================================== numeric anchoring (C# SshErrorCode)

TEST(SshErrorCodeTest, MappingMatchesCSharpTable)
{
    EXPECT_EQ(toSshErrorCode(SshSessionError::None), 0);
    EXPECT_EQ(toSshErrorCode(SshSessionError::DnsResolutionFailed), 101);
    EXPECT_EQ(toSshErrorCode(SshSessionError::ConnectTimeout), 102);
    EXPECT_EQ(toSshErrorCode(SshSessionError::ConnectionRefused), 103);
    EXPECT_EQ(toSshErrorCode(SshSessionError::NetworkUnreachable), 104);
    EXPECT_EQ(toSshErrorCode(SshSessionError::AuthFailedPassword), 201);
    EXPECT_EQ(toSshErrorCode(SshSessionError::AuthFailedKey), 202);
    EXPECT_EQ(toSshErrorCode(SshSessionError::AuthFailedInteractive), 203);
    EXPECT_EQ(toSshErrorCode(SshSessionError::AuthFailedPassphrase), 204);
    EXPECT_EQ(toSshErrorCode(SshSessionError::AuthTimeout), 205);
    EXPECT_EQ(toSshErrorCode(SshSessionError::NoLocalCredential), 206);
    EXPECT_EQ(toSshErrorCode(SshSessionError::AlgorithmNegotiationFailed), 301);
    EXPECT_EQ(toSshErrorCode(SshSessionError::HostKeyMismatch), 303);
    EXPECT_EQ(toSshErrorCode(SshSessionError::HandshakeFailed), 304);
    EXPECT_EQ(toSshErrorCode(SshSessionError::HandshakeTimeout), 305);
    EXPECT_EQ(toSshErrorCode(SshSessionError::RemoteClosed), 401);
    EXPECT_EQ(toSshErrorCode(SshSessionError::SocketError), 403);
    EXPECT_EQ(toSshErrorCode(SshSessionError::KeepaliveTimeout), 404);
    EXPECT_EQ(toSshErrorCode(SshSessionError::InternalError), 500);
    // 302 UnknownHostKey / 402 SessionTimeout / 405 PolicyDisconnect: not
    // produced by native (TOFU confirmation and policy disconnects are
    // orchestrated in Core; generic session timeout yields to finer codes) —
    // they exist only in the C# table, and error_codes.h carries the
    // constants so the contract check passes.
    EXPECT_EQ(codes::kSshErrorCodeUnknownHostKey, 302);
    EXPECT_EQ(codes::kSshErrorCodeSessionTimeout, 402);
    EXPECT_EQ(codes::kSshErrorCodePolicyDisconnect, 405);
    // 6xx SFTP (F01): produced by the SFTP layer directly as unified codes
    // (never through SshSessionError); anchored here so the numbers cannot
    // drift from SshErrorCode.cs (the ps1 check covers the names).
    EXPECT_EQ(codes::kSshErrorCodeSftpInitFailed, 601);
    EXPECT_EQ(codes::kSshErrorCodeSftpNoSuchFile, 602);
    EXPECT_EQ(codes::kSshErrorCodeSftpPermissionDenied, 603);
    EXPECT_EQ(codes::kSshErrorCodeSftpAlreadyExists, 604);
    EXPECT_EQ(codes::kSshErrorCodeSftpTransferFailed, 605);
    EXPECT_EQ(codes::kSshErrorCodeSftpCancelled, 606);
    EXPECT_EQ(codes::kSshErrorCodeUnknown, 999);
}

// ================================================================== libssh2 fallback mapping

TEST(Libssh2ErrorMapTest, TimeoutClass)
{
    EXPECT_EQ(toSshErrorCodeFromLibssh2(LIBSSH2_ERROR_SOCKET_TIMEOUT),
              codes::kSshErrorCodeSessionTimeout);
}

TEST(Libssh2ErrorMapTest, AuthClass)
{
    EXPECT_EQ(toSshErrorCodeFromLibssh2(LIBSSH2_ERROR_AUTHENTICATION_FAILED),
              codes::kSshErrorCodeAuthPasswordFailed);
    EXPECT_EQ(toSshErrorCodeFromLibssh2(LIBSSH2_ERROR_PASSWORD_EXPIRED),
              codes::kSshErrorCodeAuthPasswordFailed);
    EXPECT_EQ(toSshErrorCodeFromLibssh2(LIBSSH2_ERROR_PUBLICKEY_UNVERIFIED),
              codes::kSshErrorCodeAuthPublicKeyFailed);
    EXPECT_EQ(toSshErrorCodeFromLibssh2(LIBSSH2_ERROR_KEYFILE_AUTH_FAILED),
              codes::kSshErrorCodePrivateKeyLoadFailed);
}

TEST(Libssh2ErrorMapTest, NegotiationAndHostKeyClass)
{
    EXPECT_EQ(toSshErrorCodeFromLibssh2(LIBSSH2_ERROR_KEX_FAILURE),
              codes::kSshErrorCodeAlgorithmNegotiationFailed);
    EXPECT_EQ(toSshErrorCodeFromLibssh2(LIBSSH2_ERROR_METHOD_NOT_SUPPORTED),
              codes::kSshErrorCodeAlgorithmNegotiationFailed);
    EXPECT_EQ(toSshErrorCodeFromLibssh2(LIBSSH2_ERROR_ALGO_UNSUPPORTED),
              codes::kSshErrorCodeAlgorithmNegotiationFailed);
    EXPECT_EQ(toSshErrorCodeFromLibssh2(LIBSSH2_ERROR_HOSTKEY_INIT),
              codes::kSshErrorCodeHostKeyMismatch);
    EXPECT_EQ(toSshErrorCodeFromLibssh2(LIBSSH2_ERROR_KNOWN_HOSTS),
              codes::kSshErrorCodeHostKeyMismatch);
}

TEST(Libssh2ErrorMapTest, SocketClass)
{
    EXPECT_EQ(toSshErrorCodeFromLibssh2(LIBSSH2_ERROR_SOCKET_DISCONNECT),
              codes::kSshErrorCodeRemoteClosed);
    EXPECT_EQ(toSshErrorCodeFromLibssh2(LIBSSH2_ERROR_SOCKET_SEND),
              codes::kSshErrorCodeSocketError);
    EXPECT_EQ(toSshErrorCodeFromLibssh2(LIBSSH2_ERROR_SOCKET_RECV),
              codes::kSshErrorCodeSocketError);
}

TEST(Libssh2ErrorMapTest, UnrecognizedFallsToUnknown)
{
    // EAGAIN is an in-progress signal, never an error code; random values too.
    EXPECT_EQ(toSshErrorCodeFromLibssh2(LIBSSH2_ERROR_EAGAIN), codes::kSshErrorCodeUnknown);
    EXPECT_EQ(toSshErrorCodeFromLibssh2(0), codes::kSshErrorCodeUnknown);
    EXPECT_EQ(toSshErrorCodeFromLibssh2(-9999), codes::kSshErrorCodeUnknown);
}

// ================================================================== toString names

TEST(SshErrorCodeTest, ToStringCoversAllErrors)
{
    for (const SshSessionError error : kAllSessionErrors) {
        const char* name = toString(error);
        ASSERT_NE(name, nullptr);
        EXPECT_STRNE(name, "unknown") << "enum value missing a toString branch";
        EXPECT_STRNE(name, "");
    }
}

} // namespace
