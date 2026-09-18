#pragma once

namespace sshclient {
namespace io {

// Process-wide Winsock lifetime. Every owner holds one reference; the first
// starts Winsock and the last performs WSACleanup.
class WinsockInit final {
public:
    WinsockInit();
    ~WinsockInit();

    WinsockInit(const WinsockInit&) = delete;
    WinsockInit& operator=(const WinsockInit&) = delete;

    bool isValid() const { return acquired_; }
    int error() const { return error_; }

    static unsigned int activeReferencesForTesting();

private:
    bool acquired_ = false;
    int error_ = 0;
};

} // namespace io
} // namespace sshclient
