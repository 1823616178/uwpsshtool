#pragma once

// Native core 同时编译进 GUI UWP 组件与桌面宿主机测试。GUI UWP 没有可用的
// stderr；调试输出必须走 OutputDebugString，否则 CRT 会触碰无效标准句柄。

#include <cstdarg>
#include <cstdio>
#include <winsock2.h>
#include <windows.h>

namespace sshclient {
namespace diagnostics {

inline void debugLog(const char* area, const char* format, ...)
{
    char message[1024]{};
    va_list args;
    va_start(args, format);
    ::vsnprintf_s(message, sizeof(message), _TRUNCATE, format, args);
    va_end(args);

#if WINAPI_FAMILY_PARTITION(WINAPI_PARTITION_DESKTOP)
    std::fprintf(stderr, "[%s] %s\n", area, message);
#else
    char line[1088]{};
    ::sprintf_s(line, sizeof(line), "[%s] %s\n", area, message);
    ::OutputDebugStringA(line);
#endif
}

} // namespace diagnostics
} // namespace sshclient
