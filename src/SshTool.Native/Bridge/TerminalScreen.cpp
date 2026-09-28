// 不用 pch：vterm.h 必须先于 Windows RPC（rpcndr.h 把 small 定义成宏）。
#include "term/vterm_screen.h"
#include "term/snapshot.h"

#include "diag_counters.h"

#include "pch.h"
#include "Bridge/TerminalScreen.h"
#include "Bridge/BridgeUtil.h"

namespace SshTool
{
    namespace Native
    {
        namespace Bridge
        {
            using sshclient::term::VtermBridge;
            using sshclient::term::copyDirtyRows;
            using sshclient::term::copyViewport;
            using sshclient::term::getText;
            using sshclient::term::MouseMode;

            struct TerminalScreen::Impl
            {
                explicit Impl(int cols, int rows) : bridge(cols, rows) {}
                VtermBridge bridge;
            };

            static int mouseModeDec(MouseMode mode)
            {
                switch (mode)
                {
                case MouseMode::kClick: return 1000;
                case MouseMode::kDrag: return 1002;
                case MouseMode::kMove: return 1003;
                default: return 0;
                }
            }

            TerminalScreen::TerminalScreen()
                : impl_(new Impl(80, 24))
            {
                sshclient::diagnostics::GlobalDiagCounters().nativeScreens.fetch_add(1, std::memory_order_relaxed);
            }

            TerminalScreen::~TerminalScreen()
            {
                sshclient::diagnostics::GlobalDiagCounters().nativeScreens.fetch_sub(1, std::memory_order_relaxed);
                delete impl_;
                impl_ = nullptr;
            }

            int64 TerminalScreen::Revision::get()
            {
                std::lock_guard<std::mutex> lock(mutex_);
                return static_cast<int64>(impl_->bridge.grid().revision());
            }

            int TerminalScreen::Cols::get()
            {
                std::lock_guard<std::mutex> lock(mutex_);
                return impl_->bridge.grid().cols();
            }

            int TerminalScreen::Rows::get()
            {
                std::lock_guard<std::mutex> lock(mutex_);
                return impl_->bridge.grid().rows();
            }

            int TerminalScreen::CursorRow::get()
            {
                std::lock_guard<std::mutex> lock(mutex_);
                return impl_->bridge.cursorRow();
            }

            int TerminalScreen::CursorCol::get()
            {
                std::lock_guard<std::mutex> lock(mutex_);
                return impl_->bridge.cursorCol();
            }

            bool TerminalScreen::CursorVisible::get()
            {
                std::lock_guard<std::mutex> lock(mutex_);
                return impl_->bridge.cursorVisible();
            }

            bool TerminalScreen::AltScreen::get()
            {
                std::lock_guard<std::mutex> lock(mutex_);
                return impl_->bridge.altScreenActive();
            }

            bool TerminalScreen::AppCursorKeys::get()
            {
                std::lock_guard<std::mutex> lock(mutex_);
                return impl_->bridge.appCursorKeys();
            }

            bool TerminalScreen::BracketedPaste::get()
            {
                std::lock_guard<std::mutex> lock(mutex_);
                return impl_->bridge.bracketedPaste();
            }

            int TerminalScreen::MouseMode::get()
            {
                std::lock_guard<std::mutex> lock(mutex_);
                return mouseModeDec(impl_->bridge.mouseMode());
            }

            bool TerminalScreen::MouseSgr::get()
            {
                std::lock_guard<std::mutex> lock(mutex_);
                return impl_->bridge.mouseSgr();
            }

            int TerminalScreen::ScrollbackCount::get()
            {
                std::lock_guard<std::mutex> lock(mutex_);
                return static_cast<int>(impl_->bridge.scrollback().size());
            }

            int64 TerminalScreen::BellCount::get()
            {
                std::lock_guard<std::mutex> lock(mutex_);
                return static_cast<int64>(impl_->bridge.bellCount());
            }

            bool TerminalScreen::CopyDirtyRows(Platform::WriteOnlyArray<uint8>^ rowsOut,
                                               Platform::WriteOnlyArray<uint8>^ dirtyOut)
            {
                if (rowsOut == nullptr || dirtyOut == nullptr)
                    return false;
                std::lock_guard<std::mutex> lock(mutex_);
                return copyDirtyRows(impl_->bridge.grid(),
                                     rowsOut->Data, rowsOut->Length,
                                     dirtyOut->Data, dirtyOut->Length);
            }

            void TerminalScreen::CopyViewport(int offset, Platform::WriteOnlyArray<uint8>^ rowsOut)
            {
                if (rowsOut == nullptr)
                    return;
                std::lock_guard<std::mutex> lock(mutex_);
                copyViewport(impl_->bridge.grid(), impl_->bridge.scrollback(), offset,
                             rowsOut->Data, rowsOut->Length);
            }

            Platform::String^ TerminalScreen::GetText(int startRow, int startCol, int endRow, int endCol,
                                                      int offset)
            {
                std::lock_guard<std::mutex> lock(mutex_);
                const std::string text = getText(impl_->bridge.grid(), impl_->bridge.scrollback(),
                                                 startRow, startCol, endRow, endCol, offset);
                return ToPlatform(text);
            }

            void TerminalScreen::Feed(const char *data, size_t len)
            {
                if (data == nullptr || len == 0)
                    return;
                std::lock_guard<std::mutex> lock(mutex_);
                impl_->bridge.feed(data, len);
            }

            void TerminalScreen::FeedBytes(const Platform::Array<uint8>^ data)
            {
                if (data == nullptr || data->Length == 0)
                    return;
                Feed(reinterpret_cast<const char *>(data->Data), data->Length);
            }

            void TerminalScreen::ResizeGrid(int cols, int rows)
            {
                ResetGrid(cols, rows);
            }

            void TerminalScreen::ResetGrid(int cols, int rows)
            {
                if (cols <= 0 || rows <= 0)
                    return;
                std::lock_guard<std::mutex> lock(mutex_);
                impl_->bridge.resize(cols, rows);
            }
        }
    }
}
