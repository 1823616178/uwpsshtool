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
