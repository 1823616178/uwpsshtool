#include "SessionThread.h"

#include <windows.h>

namespace sshclient {
namespace io {

SessionThread::~SessionThread()
{
    stop();
}

bool SessionThread::start()
{
    std::lock_guard<std::mutex> lock(mutex_);
    if (thread_.joinable()) {
        return true;
    }
    if (!loop_.isValid()) {
        return false;
    }

    loop_.rearm();
    thread_ = std::thread([this] {
        ::SetThreadDescription(::GetCurrentThread(), L"Lumia SSH session");
        loop_.run();
    });
    return true;
}

void SessionThread::stop()
{
    std::lock_guard<std::mutex> lock(mutex_);
    if (thread_.joinable()) {
        loop_.stop();
        thread_.join();
    }
    loop_.clearPendingTasks();
}

bool SessionThread::isRunning() const
{
    std::lock_guard<std::mutex> lock(mutex_);
    return thread_.joinable();
}

} // namespace io
} // namespace sshclient
