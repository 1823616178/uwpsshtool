#include "diag_counters.h"

namespace sshclient {
namespace diagnostics {

DiagCounters& GlobalDiagCounters()
{
    static DiagCounters counters;
    return counters;
}

std::string DiagCountersSnapshot()
{
    DiagCounters& c = GlobalDiagCounters();
    std::string s = "sessions=" + std::to_string(c.SshSessions());
    s += " threads=" + std::to_string(c.SessionThreads());
    s += " sockets=" + std::to_string(c.EventLoopSockets());
    s += " screens=" + std::to_string(c.NativeScreens());
    return s;
}

} // namespace diagnostics
} // namespace sshclient
