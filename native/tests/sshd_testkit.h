#pragma once

#include "ssh/session.h"

#include <gtest/gtest.h>

#include <chrono>
#include <condition_variable>
#include <cstdlib>
#include <mutex>
#include <string>
#include <utility>
#include <vector>

namespace {

class StateRecorder final {
public:
    void operator()(sshclient::ssh::SshSessionState from,
                    sshclient::ssh::SshSessionState to)
    {
        {
            std::lock_guard<std::mutex> lock(mutex_);
            transitions_.emplace_back(from, to);
        }
        changed_.notify_all();
    }

    bool waitFor(sshclient::ssh::SshSessionState target,
                 std::chrono::milliseconds timeout)
    {
        std::unique_lock<std::mutex> lock(mutex_);
        return changed_.wait_for(lock, timeout, [&] {
            for (const auto& transition : transitions_) {
                if (transition.second == target) {
                    return true;
                }
            }
            return false;
        });
    }

    std::vector<sshclient::ssh::SshSessionState> sequence() const
    {
        std::lock_guard<std::mutex> lock(mutex_);
        std::vector<sshclient::ssh::SshSessionState> result;
        for (const auto& transition : transitions_) {
            result.push_back(transition.second);
        }
        return result;
    }

private:
    mutable std::mutex mutex_;
    std::condition_variable changed_;
    std::vector<std::pair<sshclient::ssh::SshSessionState,
                          sshclient::ssh::SshSessionState>> transitions_;
};

struct SshIntegrationEnvironment {
    std::string host;
    std::uint16_t port = 0;
    std::string user;
    std::string password;
};

bool LoadSshIntegrationEnvironment(SshIntegrationEnvironment& environment)
{
    const char* host = std::getenv("SSH_TEST_HOST");
    const char* port = std::getenv("SSH_TEST_PORT");
    const char* user = std::getenv("SSH_TEST_USER");
    const char* password = std::getenv("SSH_TEST_PASSWORD");
    if (host == nullptr || host[0] == '\0' || port == nullptr || port[0] == '\0' ||
        user == nullptr || user[0] == '\0' || password == nullptr || password[0] == '\0') {
        return false;
    }

    try {
        const int parsedPort = std::stoi(port);
        if (parsedPort < 1 || parsedPort > 65535) {
            return false;
        }
        environment.port = static_cast<std::uint16_t>(parsedPort);
    } catch (...) {
        return false;
    }
    environment.host = host;
    environment.user = user;
    environment.password = password;
    return true;
}

// Whether the recorder ever reached the given state.
bool Visited(const StateRecorder& recorder, sshclient::ssh::SshSessionState state)
{
    for (const sshclient::ssh::SshSessionState visited : recorder.sequence()) {
        if (visited == state) {
            return true;
        }
    }
    return false;
}

} // namespace
