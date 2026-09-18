#pragma once

// N09a：WinRT 桥内部共用工具（String^ ↔ UTF-8 std::string）。
// 仅供 SshTool.Native 程序集内部使用，不进 winmd。

#include <string>
#include <vector>

namespace SshTool
{
    namespace Native
    {
        namespace Bridge
        {
            inline std::string ToUtf8(Platform::String^ value)
            {
                if (value == nullptr || value->IsEmpty()) { return std::string(); }
                const wchar_t* data = value->Data();
                const int needed = ::WideCharToMultiByte(CP_UTF8, 0, data, (int)value->Length(),
                                                         nullptr, 0, nullptr, nullptr);
                std::string out((size_t)needed, '\0');
                if (needed > 0)
                {
                    ::WideCharToMultiByte(CP_UTF8, 0, data, (int)value->Length(), &out[0],
                                          needed, nullptr, nullptr);
                }
                return out;
            }

            inline Platform::String^ ToPlatform(const std::string& value)
            {
                if (value.empty()) { return ref new Platform::String(L""); }
                const int needed = ::MultiByteToWideChar(CP_UTF8, 0, value.c_str(),
                                                         (int)value.size(), nullptr, 0);
                std::wstring out((size_t)needed, L'\0');
                if (needed > 0)
                {
                    ::MultiByteToWideChar(CP_UTF8, 0, value.c_str(), (int)value.size(),
                                          &out[0], needed);
                }
                return ref new Platform::String(out.c_str(), (unsigned int)out.size());
            }

            inline std::vector<std::string> ToUtf8Vector(
                Windows::Foundation::Collections::IVectorView<Platform::String^>^ values)
            {
                std::vector<std::string> out;
                if (values == nullptr) { return out; }
                for (Platform::String^ value : values)
                {
                    out.push_back(ToUtf8(value));
                }
                return out;
            }
        }
    }
}
