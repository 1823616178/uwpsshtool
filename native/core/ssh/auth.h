#pragma once

// N05: authentication — password / public key (in-memory private key) /
// keyboard-interactive (01-DESIGN.md section 9.2, depends on N04).
//
// Split of duties:
//   - SshSession's auth drivers (authenticate* / queryAuthMethods / driveAuth
//     and friends) are declared in session.h and implemented in auth.cpp
//     (member functions split across files to keep session.cpp lean);
//   - this header carries only pure logic helpers, unit-testable offline:
//       secureZero          wipe that survives compiler optimization;
//       parseAuthMethodList parse the userauth_list CSV;
//       mapAuthError        libssh2 userauth return code -> SshSessionError.
//
// secureZero uses OPENSSL_cleanse: Windows has no explicit_bzero and UWP's
// old CRT makes memset_s unreliable, while the project already links OpenSSL.
// OPENSSL_cleanse writes through a volatile pointer so the compiler must not
// eliminate it (and falls back to assembly where available).
//
// Pure logic: standard library, OpenSSL crypto and libssh2 public headers.

#include <cstddef>
#include <string>

#include "session.h"

namespace sshclient {
namespace ssh {

// Optimization-proof memory wipe. Sensitive material (password / private key /
// passphrase / KI answers) must go through this once consumed — plain memset
// may be eliminated as a dead store.
void secureZero(void* data, size_t len);
// std::string overload: wipes [0, size()) and clear()s. Contract: a credential
// string is never shrunk and reused after being assigned.
void secureZero(std::string& s);

// Parse the comma-separated list from libssh2_userauth_list (e.g.
// "publickey,password,keyboard-interactive"). Items are trimmed, empty items
// ignored, unrecognized methods land in `unsupported`. Pure logic.
AuthMethodSet parseAuthMethodList(const std::string& csv);

// libssh2 userauth return code -> fine-grained SshSessionError (pure logic).
// Rules (verified line-by-line against libssh2 1.11.1 pem.c/openssl.c/
// userauth.c plus live sshd runs; re-verify when bumping the pin — the
// integration tests will flag a drift before this table does):
//   - Password: always AuthFailedPassword (wrong password / unknown user /
//     expired password all surface as AUTHENTICATION_FAILED);
//   - PublicKey:
//       KEYFILE_AUTH_FAILED (PEM private key wrong/missing passphrase)
//         -> AuthFailedPassphrase;
//       FILE (public-key-derivation stage failed with no public key data —
//         OpenSSH-format encrypted keys with a wrong passphrase degrade to
//         this code: openssl.c swallows the inner KEYFILE_AUTH_FAILED there;
//         garbage keys share the code but have a different message and are
//         equally "local key unusable" for the UI) -> AuthFailedPassphrase;
//       PUBLICKEY_UNVERIFIED with message containing "Callback returned error"
//         (local decrypt failed at the signing stage with public key data —
//         userauth.c:1771 swallows the sign callback's real error)
//         -> AuthFailedPassphrase;
//       everything else (AUTHENTICATION_FAILED = server rejected the key,
//       PUBLICKEY_UNVERIFIED = server-side signature check failed, ...)
//         -> AuthFailedKey;
//   - KeyboardInteractive: always AuthFailedInteractive;
//   transport-level failures are covered by the session's disconnect
//   detection and never masked as auth codes.
SshSessionError mapAuthError(int libssh2Error, AuthMethod method, const std::string& message);

const char* toString(AuthMethod method);

} // namespace ssh
} // namespace sshclient
