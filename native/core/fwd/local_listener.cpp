#include "fwd/local_listener.h"

#include "debug_log.h"
#include "io/SessionThread.h"
#include "ssh/session.h"

#include <ws2tcpip.h>

#include <utility>
#include <vector>

#define FWD_LOG(...) sshclient::diagnostics::debugLog("fwd", __VA_ARGS__)

namespace sshclient {
namespace fwd {

LocalListener::LocalListener(ssh::SshSession& session, std::string destHost,
                             std::uint16_t destPort, std::shared_ptr<TunnelStats> stats,
                             LocalListenerCallbacks callbacks)
    : session_(session)
    , destHost_(std::move(destHost))
    , destPort_(destPort)
    , stats_(std::move(stats))
    , callbacks_(std::move(callbacks))
{
    if (!stats_) {
        stats_ = std::make_shared<TunnelStats>();
    }
}

LocalListener::~LocalListener()
{
    if (listenSocket_ != INVALID_SOCKET) {
        FWD_LOG("warning: LocalListener destroyed with a live listen socket "
                "(contract violation, leak)");
    }
    if (!connections_.empty()) {
        FWD_LOG("warning: LocalListener destroyed with %u live connections "
                "(contract violation, leak)",
                static_cast<unsigned>(connections_.size()));
    }
}

bool LocalListener::start(std::string listenHost, std::uint16_t listenPort)
{
    if (destHost_.empty() || destPort_ == 0) {
        FWD_LOG("local listen rejected: no direct-tcpip destination configured");
        return false;
    }
    if (session_.state() != ssh::SshSessionState::Established) {
        FWD_LOG("local listen rejected: session state %s (only established admits)",
                ssh::toString(session_.state()));
        return false;
    }
    bool expected = false;
    if (!startAdmitted_.compare_exchange_strong(expected, true)) {
        FWD_LOG("local listen rejected: already started");
        return false;
    }
    listenHost_ = std::move(listenHost);
    listenPort_ = listenPort;
    session_.thread_.post([this] { begin(); });
    return true;
}

void LocalListener::stop()
{
    bool expected = false;
    if (!stopRequested_.compare_exchange_strong(expected, true)) {
        return; // idempotent
    }
    session_.thread_.post([this] {
        if (state_.load(std::memory_order_acquire) == LocalListenState::Listening) {
            teardown("local stop requested");
        }
        // Idle (begin never ran / start failed): nothing to tear down, and no
        // onStopped (contract: onStopped only follows a successful start).
    });
}

void LocalListener::begin()
{
    if (session_.state() != ssh::SshSessionState::Established ||
        session_.session_ == nullptr) {
        startAdmitted_.store(false, std::memory_order_release); // allow a retry
        if (callbacks_.onListening) {
            callbacks_.onListening({false, 0, "session left the established state"});
        }
        return;
    }
    addrinfo hints{};
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    hints.ai_protocol = IPPROTO_TCP;
    hints.ai_flags = AI_PASSIVE;
    const char* node = listenHost_.empty() ? nullptr : listenHost_.c_str();
    const std::string service = std::to_string(listenPort_);
    addrinfo* addresses = nullptr;
    if (::getaddrinfo(node, service.c_str(), &hints, &addresses) != 0) {
        startAdmitted_.store(false, std::memory_order_release);
        if (callbacks_.onListening) {
            callbacks_.onListening({false, 0, "listen DNS resolution failed: " + listenHost_});
        }
        return;
    }
    SOCKET listener = INVALID_SOCKET;
    std::uint16_t boundPort = 0;
    int lastError = WSAEADDRNOTAVAIL;
    for (addrinfo* address = addresses; address != nullptr; address = address->ai_next) {
        SOCKET candidate = ::socket(address->ai_family, address->ai_socktype,
                                    address->ai_protocol);
        if (candidate == INVALID_SOCKET) {
            lastError = ::WSAGetLastError();
            continue;
        }
        u_long nonBlocking = 1;
        if (::ioctlsocket(candidate, FIONBIO, &nonBlocking) == SOCKET_ERROR) {
            lastError = ::WSAGetLastError();
            ::closesocket(candidate);
            continue;
        }
        const BOOL reuse = TRUE;
        ::setsockopt(candidate, SOL_SOCKET, SO_REUSEADDR,
                     reinterpret_cast<const char*>(&reuse), sizeof(reuse));
        if (::bind(candidate, address->ai_addr, static_cast<int>(address->ai_addrlen)) != 0) {
            lastError = ::WSAGetLastError();
            ::closesocket(candidate);
            continue;
        }
        if (::listen(candidate, SOMAXCONN) != 0) {
            lastError = ::WSAGetLastError();
            ::closesocket(candidate);
            continue;
        }
        // Actual port (ephemeral when 0 was requested).
        sockaddr_storage bound{};
        int boundLen = sizeof(bound);
        if (::getsockname(candidate, reinterpret_cast<sockaddr*>(&bound), &boundLen) == 0) {
            if (bound.ss_family == AF_INET) {
                boundPort = ::ntohs(reinterpret_cast<sockaddr_in*>(&bound)->sin_port);
            } else if (bound.ss_family == AF_INET6) {
                boundPort = ::ntohs(reinterpret_cast<sockaddr_in6*>(&bound)->sin6_port);
            }
        }
        listener = candidate;
        break;
    }
    ::freeaddrinfo(addresses);
    if (listener == INVALID_SOCKET) {
        startAdmitted_.store(false, std::memory_order_release);
        char message[128]{};
        ::sprintf_s(message, sizeof(message), "listen bind failed (WSA %d)", lastError);
        if (callbacks_.onListening) {
            callbacks_.onListening({false, 0, message});
        }
        return;
    }
    listenSocket_ = listener;
    boundPort_ = boundPort;
    if (!session_.thread_.loop().addSocket(
            listenSocket_, io::EventLoop::Readable,
            [this](SOCKET socket, short events) { onListenEvent(socket, events); })) {
        ::closesocket(listenSocket_);
        listenSocket_ = INVALID_SOCKET;
        startAdmitted_.store(false, std::memory_order_release);
        if (callbacks_.onListening) {
            callbacks_.onListening({false, 0, "failed to register listen socket"});
        }
        return;
    }
    listenRegistered_ = true;
    state_.store(LocalListenState::Listening, std::memory_order_release);
    session_.registerLocalListener(this);
    FWD_LOG("local listening on %s:%u -> %s:%u",
            listenHost_.empty() ? "*" : listenHost_.c_str(), boundPort_,
            destHost_.c_str(), destPort_);
    if (callbacks_.onListening) {
        callbacks_.onListening({true, boundPort_, ""});
    }
    if (stopRequested_.load(std::memory_order_acquire)) {
        teardown("stop raced the listen bind");
    }
}

void LocalListener::onListenEvent(SOCKET socket, short events)
{
    (void)events;
    if (socket != listenSocket_ ||
        state_.load(std::memory_order_acquire) != LocalListenState::Listening) {
        return;
    }
    acceptBurst();
}

void LocalListener::acceptBurst()
{
    for (;;) {
        sockaddr_storage peer{};
        int peerLen = sizeof(peer);
        SOCKET accepted = ::accept(listenSocket_, reinterpret_cast<sockaddr*>(&peer),
                                   &peerLen);
        if (accepted == INVALID_SOCKET) {
            const int error = ::WSAGetLastError();
            if (error != WSAEWOULDBLOCK) {
                FWD_LOG("accept failed (WSA %d)", error);
            }
            return; // drained (or transient error: next event retries)
        }
        u_long nonBlocking = 1;
        if (::ioctlsocket(accepted, FIONBIO, &nonBlocking) == SOCKET_ERROR) {
            ::closesocket(accepted);
            continue;
        }
        spawnConnection(accepted);
    }
}

void LocalListener::spawnConnection(SOCKET accepted)
{
    if (session_.state() != ssh::SshSessionState::Established) {
        // Session lost while listening: refuse fast rather than queueing a
        // doomed open (the tunnel is re-armed by F05 after reconnect).
        FWD_LOG("closing accepted client: session not established");
        ::closesocket(accepted);
        return;
    }
    // Originator address for the direct-tcpip shost/sport (best effort;
    // failures fall back to 127.0.0.1:0 at open time).
    std::string srcHost = "127.0.0.1";
    std::uint16_t srcPort = 0;
    sockaddr_storage peer{};
    int peerLen = sizeof(peer);
    if (::getpeername(accepted, reinterpret_cast<sockaddr*>(&peer), &peerLen) == 0) {
        char text[INET6_ADDRSTRLEN]{};
        if (peer.ss_family == AF_INET) {
            const sockaddr_in* v4 = reinterpret_cast<const sockaddr_in*>(&peer);
            if (::InetNtopA(AF_INET, &v4->sin_addr, text, sizeof(text)) != nullptr) {
                srcHost = text;
            }
            srcPort = ::ntohs(v4->sin_port);
        } else if (peer.ss_family == AF_INET6) {
            const sockaddr_in6* v6 = reinterpret_cast<const sockaddr_in6*>(&peer);
            if (::InetNtopA(AF_INET6, &v6->sin6_addr, text, sizeof(text)) != nullptr) {
                srcHost = text;
            }
            srcPort = ::ntohs(v6->sin6_port);
        }
    }
    ForwardedCallbacks callbacks;
    callbacks.onOpen = [](const ForwardOpenResult&) {}; // stats flow regardless
    auto owned =
        std::make_unique<ForwardedConnection>(session_, accepted, stats_, std::move(callbacks));
    ForwardedConnection* raw = owned.get();
    // Erase-by-pointer on close: raw is stable (heap) and set before any
    // loop-thread callback can fire (all callbacks serialize after this very
    // function on the same loop thread), so capture by value is sound.
    owned->setCloseCallback([this, raw](const ForwardCloseInfo&) {
        for (auto it = connections_.begin(); it != connections_.end(); ++it) {
            if (it->get() == raw) {
                connections_.erase(it); // destroys post-onClose: contract holds
                break;
            }
        }
    });
    if (!owned->openLocal(destHost_, destPort_, srcHost, srcPort)) {
        return; // admission refused (session raced away): the Idle destructor
                // closes the never-registered socket, no leak
    }
    connections_.insert(std::move(owned));
}

void LocalListener::teardown(const char* reason)
{
    FWD_LOG("local listener teardown: %s (%u live connections)", reason,
            static_cast<unsigned>(connections_.size()));
    session_.unregisterLocalListener(this);
    if (listenRegistered_) {
        session_.thread_.loop().removeSocket(listenSocket_);
        listenRegistered_ = false;
    }
    if (listenSocket_ != INVALID_SOCKET) {
        ::closesocket(listenSocket_);
        listenSocket_ = INVALID_SOCKET;
    }
    // Graceful per-connection close: each drains through Closing and erases
    // itself in onClose (snapshot: onClose mutates the set).
    const std::vector<ForwardedConnection*> snapshot = [this] {
        std::vector<ForwardedConnection*> out;
        for (const auto& owned : connections_) {
            out.push_back(owned.get());
        }
        return out;
    }();
    for (ForwardedConnection* connection : snapshot) {
        connection->close();
    }
    state_.store(LocalListenState::Stopped, std::memory_order_release);
    if (callbacks_.onStopped) {
        callbacks_.onStopped();
    }
}

void LocalListener::onSessionLost()
{
    // Loop thread from SshSession::releaseResources. Connections were already
    // force-cleaned (notify order: connections first) and erased themselves
    // via onClose; only the listen socket remains.
    if (listenRegistered_) {
        session_.thread_.loop().removeSocket(listenSocket_);
        listenRegistered_ = false;
    }
    if (listenSocket_ != INVALID_SOCKET) {
        ::closesocket(listenSocket_);
        listenSocket_ = INVALID_SOCKET;
    }
    if (!connections_.empty()) {
        FWD_LOG("warning: %u connections survived session loss (leak)",
                static_cast<unsigned>(connections_.size()));
        connections_.clear();
    }
    if (state_.load(std::memory_order_acquire) == LocalListenState::Listening) {
        state_.store(LocalListenState::Stopped, std::memory_order_release);
        // No onStopped: the session is gone and no reconnect reuses this
        // listener (F05 recreates it). Firing it would suggest usability.
    }
}

} // namespace fwd
} // namespace sshclient
