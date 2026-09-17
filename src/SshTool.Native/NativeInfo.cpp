#include "pch.h"
#include "NativeInfo.h"

#include <openssl/crypto.h>
#include <string>

using Platform::String;
using SshTool::Native::NativeInfo;

String^ NativeInfo::Version()
{
    return "0.0.1";
}

String^ NativeInfo::OpenSslVersion()
{
    const char* v = OpenSSL_version(OPENSSL_VERSION);
    std::wstring ws;
    if (v != nullptr)
    {
        while (*v != '\0')
        {
            ws += static_cast<wchar_t>(*v);
            ++v;
        }
    }
    return ref new String(ws.c_str());
}
