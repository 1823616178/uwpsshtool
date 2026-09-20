#pragma once

// T03：WinRT 终端屏幕快照（01-DESIGN §6.2）。实现藏在 cpp，避免把 vterm.h 带进 pch。

#include <cstdint>
#include <memory>
#include <mutex>
#include <string>

namespace SshTool
{
    namespace Native
    {
        namespace Bridge
        {
            public ref class TerminalScreen sealed
            {
            public:
                TerminalScreen();
                virtual ~TerminalScreen();

                property int64 Revision { int64 get(); }
                property int Cols { int get(); }
                property int Rows { int get(); }
                property int CursorRow { int get(); }
                property int CursorCol { int get(); }
                property bool CursorVisible { bool get(); }
                property bool AltScreen { bool get(); }
                property bool AppCursorKeys { bool get(); }
                property bool BracketedPaste { bool get(); }
                property int MouseMode { int get(); }
                property bool MouseSgr { bool get(); }
                property int ScrollbackCount { int get(); }

                bool CopyDirtyRows(Platform::WriteOnlyArray<uint8>^ rowsOut,
                                   Platform::WriteOnlyArray<uint8>^ dirtyOut);
                void CopyViewport(int offset, Platform::WriteOnlyArray<uint8>^ rowsOut);
                Platform::String^ GetText(int startRow, int startCol, int endRow, int endCol, int offset);

                // Q01（PerfPage 字节流直喂）：不经 SSH 灌输出流——把生成的终端输出
                // 字节流（base64 / yes / vim 分页脚本）直接喂进 vterm，走与真会话
                // 完全相同的 解析→网格→回滚 管线；PerfPage 经 NativeTerminalScreen
                // 调用。内部 Feed 签名（const char*）不是 WinRT 类型，无法直接公开。
                void FeedBytes(const Platform::Array<uint8>^ data);
                // Q01：PerfPage 按 TerminalView 实测网格重设 vterm 尺寸
                // （正常路径由 SshSession 在 OpenShell/Resize 时调 internal ResetGrid）。
                void ResizeGrid(int cols, int rows);

            internal:
                void Feed(const char *data, size_t len);
                void ResetGrid(int cols, int rows);

            private:
                struct Impl;
                Impl *impl_;
                std::mutex mutex_;
            };
        }
    }
}
