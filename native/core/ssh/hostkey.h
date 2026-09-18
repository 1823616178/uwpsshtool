#pragma once

// N04: host key extraction, fingerprints and TOFU comparison (01-DESIGN.md
// section 9.2). known_hosts persistence lives in the C# Core layer, so the
// native layer never touches storage: it extracts the host key from a
// handshaken LIBSSH2_SESSION, computes OpenSSH-compatible SHA256/MD5
// fingerprints and the Drunken Bishop randomart, and compares a presented
// fingerprint against a caller-supplied baseline (three-state result).
//
// libssh2 exposes the host key only after the handshake completes, so the
// verification point sits between handshake and userauth. A rejected key must
// never reach Authenticating: SshSession disconnects with
// SSH_DISCONNECT_HOST_KEY_NOT_VERIFIABLE and reports HostKeyMismatch (303).
//
// Pure logic: standard library, OpenSSL EVP and libssh2 public headers only.

#include <cstddef>
#include <cstdint>
#include <functional>
#include <optional>
#include <string>
#include <vector>

struct _LIBSSH2_SESSION;

namespace sshclient {
namespace ssh {

enum class HostKeyCheckResult {
    Ok,       // matches the known fingerprint
    Unknown,  // no known fingerprint to compare against (first-connect TOFU)
    Mismatch, // differs from the known fingerprint; extraction failure also
              // lands here (fail-closed)
};

// Everything a host presented, filled by extractHostKey.
struct HostKeyInfo {
    std::string keyType;           // algorithm name embedded in the blob, e.g. "ssh-ed25519"
    std::vector<uint8_t> rawKey;   // raw host key bytes (SSH public key blob)
    std::string fingerprintSha256; // "SHA256:<base64 no padding>", matches ssh-keygen -l
    std::string fingerprintMd5;    // "MD5:aa:bb:..." (legacy display only)
    std::string randomart;         // Drunken Bishop art with borders, ssh-keygen -lv style
};

// ---------------------------------------------------------------- pure logic
// No libssh2 involvement; independently unit-testable with any byte string.

// Standard base64 alphabet without trailing '=' padding (OpenSSH fingerprint form).
std::string base64EncodeNoPadding(const uint8_t* data, size_t len);

// SHA256(data) -> "SHA256:<base64 no padding>"; empty string on OpenSSL failure.
std::string fingerprintSha256(const uint8_t* data, size_t len);

// MD5(data) -> "MD5:aa:bb:..."; display-only, security decisions always use SHA256.
std::string fingerprintMd5(const uint8_t* data, size_t len);

// Drunken Bishop randomart, byte-for-byte aligned with OpenSSH sshkey.c
// fingerprint_randomart(): 17x9 field, start (8,4), 4 steps of 2 bits LSB-first
// per byte, visit cap 14, S at start / E at end, borders "+--[TITLE]--+" and
// "+----[HASH]-----+" left-biased. digest is the fingerprint digest bytes (32
// for SHA256); title looks like "[ED25519 256]", hashName like "SHA256".
// Output is 11 lines joined by '\n', no trailing newline.
std::string randomartFromDigest(const uint8_t* digest, size_t digestLen,
                                const std::string& title, const std::string& hashName);

// ---------------------------------------------------------------- session API

// Extract the session's host key. session must have completed the handshake
// (libssh2 semantics), otherwise returns nullopt.
std::optional<HostKeyInfo> extractHostKey(struct _LIBSSH2_SESSION* session);

// Pure three-state comparison: empty expected -> Unknown (first connect);
// exact equality -> Ok; anything else -> Mismatch. Both sides should use the
// "SHA256:..." format produced by fingerprintSha256().
HostKeyCheckResult checkFingerprintSha256(const std::string& actual,
                                          const std::string& expected);

// Session-level three-state check: extractHostKey + checkFingerprintSha256;
// infoOut receives the full key picture when non-null. Extraction failure
// returns Mismatch (fail-closed: a session that cannot prove its identity is
// never trusted).
HostKeyCheckResult checkHostKey(struct _LIBSSH2_SESSION* session,
                                const std::string& expectedFingerprintSha256,
                                HostKeyInfo* infoOut = nullptr);

// TOFU decision injected by the upper layer. SshSession invokes it on the
// event loop thread after a successful handshake, before Authenticating. It
// must return quickly: UI confirmation is asynchronous, so the upper layer
// orchestrates "reject -> user confirms -> persist fingerprint -> reconnect"
// instead of blocking inside the callback.
enum class HostKeyDecision {
    Accept, // continue into Authenticating
    Reject, // disconnect with HostKeyMismatch (303) via Closing -> Closed
};
using HostKeyCallback = std::function<HostKeyDecision(const HostKeyInfo& info)>;

const char* toString(HostKeyCheckResult result);

} // namespace ssh
} // namespace sshclient
