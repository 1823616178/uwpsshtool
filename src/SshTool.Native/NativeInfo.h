#pragma once

namespace SshTool
{
    namespace Native
    {
        public ref class NativeInfo sealed
        {
        public:
            static Platform::String^ Version();
            static Platform::String^ OpenSslVersion();
            // Q02：native 资源计数快照 "sessions=N threads=N sockets=N screens=N"
            //（diag_counters；调试页 PerfPage 读取并落盘 perf-q02-*.txt）。
            static Platform::String^ DiagCounters();
        };
    }
}
