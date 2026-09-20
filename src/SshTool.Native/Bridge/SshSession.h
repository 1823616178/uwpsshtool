#pragma once

// N09a：WinRT 桥 SshSession（01-DESIGN §6.2；04-TASKS N09a）。
//
// 线程模型（要点 1/4）：
//   - 每个 SshSession 拥有一条 io::SessionThread；core 的 SshSession/
//     SshChannel 全部跑在它的 I/O 循环线程上；
//   - 所有事件（StateChanged/HostKeyCheck/AuthPrompt/ContentDirty）都在
//     I/O 线程触发，C# 侧负责封送 UI 线程；
//   - core 回调经 Platform::WeakReference 回指本对象，避免「core 会话持有
//     桥句柄、桥持有 core 会话」的循环引用；桥销毁后回调落空即弃；
// 生命周期（要点 4）：Close() 幂等（CAS）；析构经 Shutdown() 幂等地 Close +
// stop()(join) + 释放 core 对象，之后 core 回调不可能再触发。注意 v141 的
// /ZW 不接受 !T() 终结器语法（C3941），C# 侧必须 using/Dispose 确定性释放
// （N09b 适配器负责）。
//
// HostKeyCheck（要点 2）：无订阅 = TOFU 直通 Accept（known_hosts 比对是
// Core 职责，N09b 适配器总会订阅）；有订阅则触发事件并在 DecisionGate 上
// 等 60 s——Accept/Reject 就地作答，或 GetDeferral 挂起窗口（挂起期间不计
// 时，最后一个 Complete 重启整窗）。超时/取消 = Reject（fail-closed，
// core 报 303）。
//
// AuthPrompt（要点 2）：事件触发后由 core 的 authPromptTimeoutMs
// （120 s 硬窗口，不因 Deferral 暂停）兜底空答复；无订阅时立即空答复，
// 避免干等。
//
// ContentDirty（要点 3）：DirtyCoalescer 合并投递——标志 0→1 才触发事件；
// C# 调 FetchPendingOutput() 拉走字节并复位标志。FetchPendingOutput 是
// 过渡取数 API，T03 TerminalScreen 接管后移除（已回写 §6.2）。
//
// §6.2 草图中 Screen/TitleChanged/Bell/ChannelClosed 属 T03 终端渲染范围，
// N09a 不实现（已回写 §6.2）。

#include "Bridge/AuthPromptEventArgs.h"
#include "Bridge/ConnectOptions.h"
#include "Bridge/HostKeyCheckEventArgs.h"
#include "Bridge/HostKeyInfo.h"
#include "Bridge/StateChangedEventArgs.h"
#include "Bridge/TerminalScreen.h"

#include "bridge_logic/decision_gate.h"
#include "bridge_logic/dirty_coalescer.h"
#include "io/SessionThread.h"
#include "ssh/channel.h"
#include "ssh/error_codes.h"
#include "ssh/session.h"

#include <atomic>
#include <cstdint>
#include <functional>
#include <memory>
#include <mutex>
#include <string>
#include <utility>
#include <vector>

// F02：core sftp 结构的前向声明（完整定义只进 cpp，避免把 libssh2 sftp
// 头带进 WinRT 公开头）。
namespace sshclient
{
    namespace sftp
    {
        struct SftpAttrs;
        struct SftpEntry;
    }
}

namespace SshTool
{
    namespace Native
    {
        namespace Bridge
        {
            // F02：SFTP 挂载状态（不透明：定义在 SshSession.cpp，持有 core
            // SftpSession 与远端文件句柄表；放命名空间作用域以便 cpp 的自由
            // 函数可用，生命周期由 SshSession 管理）。
            struct SshSessionSftpMount;
            // ExecAsync 结果（要点 5）。打开/传输失败时 ExitCode=-1、
            // Stderr 带诊断文本。
            public ref class ExecResult sealed
            {
            public:
                property int ExitCode { int get(); }
                property Platform::String^ Stdout { Platform::String^ get(); }
                property Platform::String^ Stderr { Platform::String^ get(); }

            internal:
                ExecResult(int exitCode, Platform::String^ stdoutText, Platform::String^ stderrText);

            private:
                int exitCode_;
                Platform::String^ stdout_;
                Platform::String^ stderr_;
            };

            ref class SshSession;
            ref class SshAgent; // K03：前向声明（AuthenticateAgentAsync 参数），避免头循环包含

            // core IAuthPromptSink 的桥接实现（native 类）：弱引用回指
            // SshSession。core 在 I/O 线程调用，必须只转发、不阻塞。
            class AuthPromptSinkBridge final : public sshclient::ssh::IAuthPromptSink
            {
            public:
                explicit AuthPromptSinkBridge(SshSession^ owner);
                void onAuthPrompts(std::vector<sshclient::ssh::KbdIntPrompt> prompts) override;

            private:
                Platform::WeakReference owner_;
            };

            public ref class SshSession sealed
            {
            public:
                SshSession();
                virtual ~SshSession(); // C++/CX：public 析构必须 virtual（Dispose）

                property Platform::String^ Id { Platform::String^ get(); }
                property SessionState State { SessionState get(); }
                property TerminalScreen^ Screen { TerminalScreen^ get(); }

                // 返回值是 N08 统一错误码（0 = 成功），与 C# SshErrorCode 一致。
                Windows::Foundation::IAsyncOperation<int>^ ConnectAsync(ConnectOptions^ options);
                Windows::Foundation::IAsyncOperation<int>^ AuthenticatePasswordAsync(Platform::String^ password);
                Windows::Foundation::IAsyncOperation<int>^ AuthenticatePublicKeyAsync(
                    const Platform::Array<uint8>^ privateKeyPem, Platform::String^ passphrase);
                // K03：应用内 agent 认证——只传 keyId，私钥材料由 core 直接从
                // agent 托管内存中取（不经过 WinRT/C# 层，C# 侧永不接触明文）。
                // agent 未解锁/无此 keyId/已超时 → 未受理，返回 InternalError(500)。
                Windows::Foundation::IAsyncOperation<int>^ AuthenticateAgentAsync(
                    SshAgent^ agent, Platform::String^ keyId);
                Windows::Foundation::IAsyncOperation<int>^ AuthenticateKeyboardInteractiveAsync();
                Windows::Foundation::IAsyncOperation<int>^ OpenShellAsync(int cols, int rows);
                Windows::Foundation::IAsyncOperation<ExecResult^>^ ExecAsync(Platform::String^ command);

                void Write(const Platform::Array<uint8>^ data);
                void Resize(int cols, int rows);
                void ProbeNow();
                void Close();

                // 过渡取数 API：拉走待显示输出并复位 ContentDirty 合并标志
                // （T03 TerminalScreen 接管后移除）。
                Platform::Array<uint8>^ FetchPendingOutput();

                event Windows::Foundation::EventHandler<StateChangedEventArgs^>^ StateChanged;
                event Windows::Foundation::EventHandler<Platform::Object^>^ ContentDirty;

                // 自定义事件：需要订阅计数（0 订阅 = TOFU 直通 / 立即空答复），
                // 编译器默认事件不暴露订阅数。add/remove 只计数，存储委托给
                // 私有平凡事件（托管订阅表，add 返回其 token）。
                event Windows::Foundation::EventHandler<HostKeyCheckEventArgs^>^ HostKeyCheck
                {
                    Windows::Foundation::EventRegistrationToken add(
                        Windows::Foundation::EventHandler<HostKeyCheckEventArgs^>^ handler);
                    void remove(Windows::Foundation::EventRegistrationToken token);
                internal:
                    void raise(Platform::Object^ sender, HostKeyCheckEventArgs^ args);
                }

                event Windows::Foundation::EventHandler<AuthPromptEventArgs^>^ AuthPrompt
                {
                    Windows::Foundation::EventRegistrationToken add(
                        Windows::Foundation::EventHandler<AuthPromptEventArgs^>^ handler);
                    void remove(Windows::Foundation::EventRegistrationToken token);
                internal:
                    void raise(Platform::Object^ sender, AuthPromptEventArgs^ args);
                }

            internal:
                // core 回调入口（I/O 线程，经弱引用进入）。
                sshclient::ssh::HostKeyDecision OnCoreHostKey(const sshclient::ssh::HostKeyInfo& info);
                void OnCoreAuthPrompts(std::vector<sshclient::ssh::KbdIntPrompt> prompts);

                // F02：SFTP 子系统挂载点（供 Bridge.SftpSession 调用）。
                //
                // 所有权与寿命：core SftpSession 对象 + 远端文件句柄表由本对象
                // 持有（PIMPL，见 cpp），与 core SshSession 同寿命；所有调用在
                // sessionMutex_ 下串行执行（core 调用是阻塞式的，绝不在 I/O
                // 线程上调用）；Shutdown 在同一锁内先关 SFTP 再释会话，保证
                // core 引用不悬空。返回值一律为 N08 统一错误码（0 = 成功）。
                // 取消：SftpCancel 置位原子标志，飞行中的块调用按 100 ms 切片
                // 中断（报 606）；consume-once——入口若已置位直接报 606 并清位，
                // 出口清掉中途到达的杂散置位。
                int SftpOpen(unsigned timeoutMs);
                void SftpClose();
                bool SftpIsOpen();
                int SftpListDir(const std::string& path,
                                std::vector<sshclient::sftp::SftpEntry>& entries,
                                unsigned timeoutMs);
                int SftpStat(const std::string& path, bool followSymlink,
                             sshclient::sftp::SftpAttrs& attrs, unsigned timeoutMs);
                int SftpReadLink(const std::string& path, std::string& target,
                                 unsigned timeoutMs);
                int SftpMakeDir(const std::string& path, unsigned mode, unsigned timeoutMs);
                int SftpRename(const std::string& oldPath, const std::string& newPath,
                               unsigned timeoutMs);
                int SftpRemoveFile(const std::string& path, unsigned timeoutMs);
                int SftpRemoveDir(const std::string& path, unsigned timeoutMs);
                int SftpSetPermissions(const std::string& path, unsigned mode,
                                       unsigned timeoutMs);
                int SftpOpenFile(const std::string& path, unsigned long flags, long mode,
                                 int& fileIdOut, unsigned timeoutMs);
                int SftpReadFile(int fileId, char* buffer, size_t maxLen,
                                 size_t& bytesReadOut, unsigned timeoutMs);
                int SftpWriteFile(int fileId, const char* data, size_t len,
                                  unsigned timeoutMs);
                int SftpSeekFile(int fileId, uint64_t offset, unsigned timeoutMs);
                int SftpCloseFile(int fileId, unsigned timeoutMs);
                void SftpCancel();

            private:
                void Shutdown(); // 幂等：Close + 停线程(join) + 释放 core 对象
                // F02 SFTP 拆除与准入（定义在 cpp；调用方持有 sessionMutex_）。
                void TeardownSftp();
                void TeardownSftpLocked();
                bool SftpAdmitted();
                void OnCoreStateChanged(sshclient::ssh::SshSessionState from,
                                        sshclient::ssh::SshSessionState to,
                                        std::shared_ptr<concurrency::task_completion_event<int>> connectTce);
                void OnShellData(const std::string& data);
                static SessionState MapState(sshclient::ssh::SshSessionState state);

                concurrency::task<int> DoConnect(std::string host, uint16 port, std::string username,
                                                 int connectTimeoutMs, int keepaliveSeconds,
                                                 std::string termType,
                                                 std::vector<std::pair<std::string, std::string>> envVars);
                concurrency::task<int> DoOpenShell(int cols, int rows);
                concurrency::task<int> DoAuthenticate(
                    std::function<bool(sshclient::ssh::SshSession* session,
                                       const sshclient::ssh::AuthCallback& callback)> admit);

                Platform::String^ id_;
                std::unique_ptr<sshclient::io::SessionThread> thread_;
                AuthPromptSinkBridge sink_;

                // sessionMutex_ 保护 session_/shell_ 生命周期（创建/析构 vs
                // 任意线程访问）；锁内只做快操作（ExecAsync 例外，见其实现注释）。
                std::mutex sessionMutex_;
                std::unique_ptr<sshclient::ssh::SshSession> session_; // ConnectAsync 时才构造
                std::unique_ptr<sshclient::ssh::SshChannel> shell_;
                std::string termType_ = "xterm-256color";
                std::vector<std::pair<std::string, std::string>> envVars_; // ConnectOptions.Env 副本

                std::atomic<SessionState> state_{SessionState::Idle};
                std::atomic<bool> closed_{false};      // Close() 幂等 / 析构首步
                std::atomic<bool> shutdownDone_{false};

                std::shared_ptr<sshclient::bridge::DecisionGate<bool>> hostKeyGate_;

                // 自定义事件的存储：私有平凡事件（编译器托管订阅表与
                // token），自定义 add/remove 只做订阅计数。
                event Windows::Foundation::EventHandler<HostKeyCheckEventArgs^>^ hostKeyStore_;
                event Windows::Foundation::EventHandler<AuthPromptEventArgs^>^ authPromptStore_;
                std::atomic<int> hostKeySubscribers_{0};
                std::atomic<int> authPromptSubscribers_{0};

                // Connect/OpenShell 完成事件：Shutdown 兜底置错，防 I/O 循环
                // 停止丢弃任务后任务悬挂。
                std::shared_ptr<concurrency::task_completion_event<int>> connectTce_;
                std::shared_ptr<concurrency::task_completion_event<int>> shellOpenTce_;

                // ContentDirty 合并投递 + 待取输出缓冲。
                sshclient::bridge::DirtyCoalescer dirtyCoalescer_;
                std::mutex outputMutex_;
                std::string pendingOutput_;
                TerminalScreen^ screen_;

                // F02：SFTP 挂载状态（构造时创建、Shutdown 时先行拆除）。
                SshSessionSftpMount* sftpMount_;
            };
        }
    }
}
