#include "WinsockInit.h"

#include <winsock2.h>

#include <mutex>

namespace sshclient {
namespace io {
namespace {

std::mutex g_winsockMutex;
unsigned int g_winsockReferences = 0;

} // namespace

WinsockInit::WinsockInit()
{
    std::lock_guard<std::mutex> lock(g_winsockMutex);
    if (g_winsockReferences == 0) {
        WSADATA data{};
        error_ = ::WSAStartup(MAKEWORD(2, 2), &data);
        if (error_ != 0) {
            return;
        }
    }

    ++g_winsockReferences;
    acquired_ = true;
}

WinsockInit::~WinsockInit()
{
    if (!acquired_) {
        return;
    }

    std::lock_guard<std::mutex> lock(g_winsockMutex);
    if (--g_winsockReferences == 0) {
        ::WSACleanup();
    }
}

unsigned int WinsockInit::activeReferencesForTesting()
{
    std::lock_guard<std::mutex> lock(g_winsockMutex);
    return g_winsockReferences;
}

} // namespace io
} // namespace sshclient
