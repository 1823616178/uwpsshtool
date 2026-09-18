#include "pch.h"
#include "Bridge/SshSession.h"

#include "Bridge/BridgeUtil.h"

#include <cstring>

namespace SshTool
{
    namespace Native
    {
        namespace Bridge
        {
            namespace ssh = sshclient::ssh;
            namespace bridge = sshclient::bridge;
            using concurrency::task;
            using concurrency::task_completion_event;
            using Windows::Foundation::EventHandler;
            using Windows::Foundation::EventRegistrationToken;

            // N09a 要点 2 的超时（毫秒）：HostKeyCheck 60 s / KI 120 s。
            static constexpr unsigned kHostKeyDecisionTimeoutMs = 60000;
            static constexpr unsigned kAuthPromptTimeoutMs = 120000;
            static constexpr unsigned kExecTimeoutMs = 30000;

            // ---------------------------------------------------------------- ExecResult

            ExecResult::ExecResult(int exitCode, Platform::String^ stdoutText,
                                   Platform::String^ stderrText)
                : exitCode_(exitCode), stdout_(stdoutText), stderr_(stderrText)
            {
            }

            int ExecResult::ExitCode::get() { return exitCode_; }
            Platform::String^ ExecResult::Stdout::get() { return stdout_; }
            Platform::String^ ExecResult::Stderr::get() { return stderr_; }

            // ---------------------------------------------------------------- AuthPromptSinkBridge

            AuthPromptSinkBridge::AuthPromptSinkBridge(SshSession^ owner) : owner_(owner) {}

            void AuthPromptSinkBridge::onAuthPrompts(std::vector<ssh::KbdIntPrompt> prompts)
            {
                SshSession^ self = owner_.Resolve<SshSession>();
                if (self == nullptr)
                {
                    return; // 桥已销毁：core 的 120 s 窗口兜底空答复
                }
                self->OnCoreAuthPrompts(std::move(prompts));
            }

            // ---------------------------------------------------------------- 生命周期

            SshSession::SshSession()
                : thread_(std::make_unique<sshclient::io::SessionThread>()),
                  sink_(this),
                  hostKeyGate_(std::make_shared<bridge::DecisionGate<bool>>())
            {
                // Id 无须全局唯一持久：会话内区分即可（计数 + 启动滴答）。
                static std::atomic<long long> idCounter{0};
                std::wstring idText = L"ssh-" + std::to_wstring(++idCounter) + L"-" +
                                      std::to_wstring(::GetTickCount64());
                id_ = ref new Platform::String(idText.c_str(),
                                               static_cast<unsigned int>(idText.size()));
                thread_->start();
                screen_ = ref new TerminalScreen();
            }

            SshSession::~SshSession() { Shutdown(); }

            Platform::String^ SshSession::Id::get() { return id_; }
            SessionState SshSession::State::get() { return state_.load(); }
            TerminalScreen^ SshSession::Screen::get() { return screen_; }

            void SshSession::Close()
            {
                if (closed_.exchange(true))
                {
                    return; // 幂等
                }
                std::lock_guard<std::mutex> lock(sessionMutex_);
                if (shell_ != nullptr)
                {
                    shell_->close();
                }
                if (session_ != nullptr)
                {
                    session_->close();
                }
            }

            void SshSession::Shutdown()
            {
                if (shutdownDone_.exchange(true))
                {
                    return;
                }
                Close(); // 幂等；关闭握手异步跑在 I/O 线程上
                // 兜底完成悬挂的 Connect/OpenShell 任务：EventLoop::stop 丢弃
                // 未跑的任务，回调里的 tce->set 可能永远不会来。
                {
                    std::lock_guard<std::mutex> lock(sessionMutex_);
                    if (connectTce_ != nullptr)
                    {
                        connectTce_->set(ssh::kSshErrorCodeInternalError);
                    }
                    if (shellOpenTce_ != nullptr)
                    {
                        shellOpenTce_->set(ssh::kSshErrorCodeInternalError);
                    }
                }
                thread_->stop(); // join；此后 core 回调不再触发
                std::lock_guard<std::mutex> lock(sessionMutex_);
                shell_.reset();
                session_.reset();
            }

            // ---------------------------------------------------------------- 自定义事件

            EventRegistrationToken SshSession::HostKeyCheck::add(EventHandler<HostKeyCheckEventArgs^>^ handler)
            {
                ++hostKeySubscribers_;
                return hostKeyStore_ += handler;
            }

            void SshSession::HostKeyCheck::remove(EventRegistrationToken token)
            {
                --hostKeySubscribers_;
                hostKeyStore_ -= token;
            }

            void SshSession::HostKeyCheck::raise(Platform::Object^ sender, HostKeyCheckEventArgs^ args)
            {
                hostKeyStore_(sender, args);
            }

            EventRegistrationToken SshSession::AuthPrompt::add(EventHandler<AuthPromptEventArgs^>^ handler)
            {
                ++authPromptSubscribers_;
                return authPromptStore_ += handler;
            }

            void SshSession::AuthPrompt::remove(EventRegistrationToken token)
            {
                --authPromptSubscribers_;
                authPromptStore_ -= token;
            }

            void SshSession::AuthPrompt::raise(Platform::Object^ sender, AuthPromptEventArgs^ args)
            {
                authPromptStore_(sender, args);
            }

            // ---------------------------------------------------------------- 状态

            SessionState SshSession::MapState(ssh::SshSessionState state)
            {
                switch (state)
                {
                case ssh::SshSessionState::Idle:           return SessionState::Idle;
                case ssh::SshSessionState::Connecting:     return SessionState::Connecting;
                case ssh::SshSessionState::Handshaking:    return SessionState::Handshaking;
                case ssh::SshSessionState::Authenticating: return SessionState::Authenticating;
                case ssh::SshSessionState::Established:    return SessionState::Established;
                case ssh::SshSessionState::Closing:        return SessionState::Disconnected; // 不上抛（调用方过滤）
                case ssh::SshSessionState::Closed:         return SessionState::Disconnected;
                case ssh::SshSessionState::Disconnected:   return SessionState::Disconnected;
                case ssh::SshSessionState::Error:          return SessionState::Error;
                }
                return SessionState::Error;
            }

            void SshSession::OnCoreStateChanged(ssh::SshSessionState from, ssh::SshSessionState to,
                                                std::shared_ptr<task_completion_event<int>> connectTce)
            {
                (void)from;
                if (to == ssh::SshSessionState::Closing)
                {
                    return; // 瞬态不上抛
                }
                int code = 0;
                Platform::String^ detail = ref new Platform::String(L"");
                if (to == ssh::SshSessionState::Error || to == ssh::SshSessionState::Disconnected ||
                    to == ssh::SshSessionState::Closed)
                {
                    std::lock_guard<std::mutex> lock(sessionMutex_);
                    if (session_ != nullptr)
                    {
                        code = ssh::toSshErrorCode(session_->lastError());
                        detail = ToPlatform(session_->lastErrorMessage());
                    }
                }
                const SessionState mapped = MapState(to);
                state_.store(mapped);
                StateChanged(this, ref new StateChangedEventArgs(mapped, code, detail));
                if (to == ssh::SshSessionState::Authenticating)
                {
                    connectTce->set(0); // 握手 + HostKeyCheck 完成
                }
                else if (to == ssh::SshSessionState::Error || to == ssh::SshSessionState::Disconnected ||
                         to == ssh::SshSessionState::Closed)
                {
                    connectTce->set(code); // 已 set 过时幂等（首次有效）
                }
            }

            // ---------------------------------------------------------------- core 回调

            ssh::HostKeyDecision SshSession::OnCoreHostKey(const ssh::HostKeyInfo& info)
            {
                if (hostKeySubscribers_.load() == 0)
                {
                    return ssh::HostKeyDecision::Accept; // 无订阅：TOFU 直通
                }
                auto bridgeInfo = ref new HostKeyInfo(ToPlatform(info.keyType),
                                                      ToPlatform(info.fingerprintSha256),
                                                      ToPlatform(info.randomart));
                hostKeyGate_->reset();
                auto args = ref new HostKeyCheckEventArgs(bridgeInfo, hostKeyGate_);
                HostKeyCheck(this, args);
                bool accepted = false;
                const auto outcome = hostKeyGate_->wait(accepted, kHostKeyDecisionTimeoutMs);
                if (outcome == bridge::GateOutcome::Answered && accepted)
                {
                    return ssh::HostKeyDecision::Accept;
                }
                return ssh::HostKeyDecision::Reject; // Reject/取消/超时：fail-closed
            }

            void SshSession::OnCoreAuthPrompts(std::vector<ssh::KbdIntPrompt> prompts)
            {
                Platform::WeakReference weak(this);
                auto onDecision = [weak](bool /*answered*/, std::vector<std::string> answers) {
                    SshSession^ self = weak.Resolve<SshSession>();
                    if (self == nullptr)
                    {
                        return;
                    }
                    std::lock_guard<std::mutex> lock(self->sessionMutex_);
                    if (self->session_ != nullptr)
                    {
                        self->session_->submitAuthAnswers(std::move(answers)); // 空答复 = 取消
                    }
                };
                if (authPromptSubscribers_.load() == 0)
                {
                    onDecision(false, {}); // 无订阅：立即空答复，避免干等 120 s
                    return;
                }
                auto promptList = ref new Platform::Collections::Vector<Platform::String^>();
                auto echoList = ref new Platform::Collections::Vector<bool>();
                for (const auto& prompt : prompts)
                {
                    promptList->Append(ToPlatform(prompt.text));
                    echoList->Append(prompt.echo);
                }
                auto args = ref new AuthPromptEventArgs(promptList->GetView(), echoList->GetView(),
                                                        onDecision);
                AuthPrompt(this, args);
            }

            void SshSession::OnShellData(const std::string& data)
            {
                {
                    std::lock_guard<std::mutex> lock(outputMutex_);
                    pendingOutput_ += data;
                }
                if (screen_ != nullptr)
                {
                    screen_->Feed(data.data(), data.size());
                }
                if (dirtyCoalescer_.markDirty()) // 0→1 才投递，已挂起则合并
                {
                    ContentDirty(this, nullptr);
                }
            }

            // ---------------------------------------------------------------- 连接与认证

            Windows::Foundation::IAsyncOperation<int>^ SshSession::ConnectAsync(ConnectOptions^ options)
            {
                if (options == nullptr)
                {
                    throw ref new Platform::NullReferenceException();
                }
                // 调用线程快照取值，异步体不再回读 ConnectOptions。
                const std::string host = ToUtf8(options->Host);
                const int port = options->Port;
                const std::string username = ToUtf8(options->Username);
                const int connectTimeoutMs = options->ConnectTimeoutMs;
                const int keepaliveSeconds = options->KeepaliveSeconds;
                const std::string termType = ToUtf8(options->TermType);
                std::vector<std::pair<std::string, std::string>> envVars;
                if (options->Env != nullptr)
                {
                    for (auto pair : options->Env)
                    {
                        envVars.push_back({ToUtf8(pair->Key), ToUtf8(pair->Value)});
                    }
                }
                SshSession^ self = this;
                return concurrency::create_async(
                    [self, host, port, username, connectTimeoutMs, keepaliveSeconds, termType, envVars]() -> task<int> {
                        return self->DoConnect(host, static_cast<uint16>(port), username,
                                               connectTimeoutMs, keepaliveSeconds, termType, envVars);
                    });
            }

            task<int> SshSession::DoConnect(std::string host, uint16 port, std::string username,
                                            int connectTimeoutMs, int keepaliveSeconds,
                                            std::string termType,
                                            std::vector<std::pair<std::string, std::string>> envVars)
            {
                if (closed_.load())
                {
                    return concurrency::task_from_result(ssh::kSshErrorCodeInternalError);
                }
                auto tce = std::make_shared<task_completion_event<int>>();
                {
                    std::lock_guard<std::mutex> lock(sessionMutex_);
                    if (session_ != nullptr)
                    {
                        return concurrency::task_from_result(ssh::kSshErrorCodeInternalError); // 重复连接
                    }
                    ssh::SshSessionOptions coreOptions;
                    coreOptions.connectTimeoutMs = static_cast<std::uint32_t>(connectTimeoutMs);
                    coreOptions.authPromptTimeoutMs = kAuthPromptTimeoutMs;
                    Platform::WeakReference weak(this);
                    coreOptions.hostKeyCallback = [weak](const ssh::HostKeyInfo& info) {
                        SshSession^ self = weak.Resolve<SshSession>();
                        if (self == nullptr)
                        {
                            return ssh::HostKeyDecision::Reject; // 桥已销毁：fail-closed
                        }
                        return self->OnCoreHostKey(info);
                    };
                    coreOptions.authPromptSink = &sink_;
                    auto stateCallback = [weak, tce](ssh::SshSessionState from, ssh::SshSessionState to) {
                        SshSession^ self = weak.Resolve<SshSession>();
                        if (self != nullptr)
                        {
                            self->OnCoreStateChanged(from, to, tce);
                        }
                        else if (to == ssh::SshSessionState::Error ||
                                 to == ssh::SshSessionState::Disconnected ||
                                 to == ssh::SshSessionState::Closed)
                        {
                            tce->set(ssh::kSshErrorCodeInternalError); // 桥已销毁：兜底
                        }
                    };
                    auto session = std::make_unique<ssh::SshSession>(*thread_, coreOptions, stateCallback);
                    if (keepaliveSeconds > 0)
                    {
                        session->setKeepaliveConfig(static_cast<std::uint32_t>(keepaliveSeconds),
                                                    ssh::kDefaultKeepaliveMaxMisses);
                    }
                    if (!session->connect(std::move(host), port, std::move(username)))
                    {
                        return concurrency::task_from_result(ssh::kSshErrorCodeInternalError); // 未受理（非 Idle）
                    }
                    termType_ = std::move(termType);
                    envVars_ = std::move(envVars);
                    connectTce_ = tce;
                    session_ = std::move(session);
                }
                return concurrency::create_task(*tce);
            }

            task<int> SshSession::DoAuthenticate(
                std::function<bool(ssh::SshSession*, const ssh::AuthCallback&)> admit)
            {
                if (closed_.load())
                {
                    return concurrency::task_from_result(ssh::kSshErrorCodeInternalError);
                }
                auto tce = std::make_shared<task_completion_event<int>>();
                ssh::AuthCallback callback = [tce](const ssh::AuthResult& result) {
                    tce->set(result.success ? 0 : ssh::toSshErrorCode(result.error));
                };
                bool admitted = false;
                {
                    std::lock_guard<std::mutex> lock(sessionMutex_);
                    admitted = session_ != nullptr && admit(session_.get(), callback);
                }
                if (!admitted)
                {
                    return concurrency::task_from_result(ssh::kSshErrorCodeInternalError);
                }
                return concurrency::create_task(*tce);
            }

            Windows::Foundation::IAsyncOperation<int>^ SshSession::AuthenticatePasswordAsync(Platform::String^ password)
            {
                SshSession^ self = this;
                return concurrency::create_async([self, pwd = ToUtf8(password)]() -> task<int> {
                    // 外层 lambda 须为 const operator()（create_async 模板推导要求）；
                    // 内层复制一份口令，受理时由 core 清空的是内层这份。
                    return self->DoAuthenticate(
                        [p = pwd](ssh::SshSession* session, const ssh::AuthCallback& cb) mutable {
                            return session->authenticatePassword(p, cb);
                        });
                });
            }

            Windows::Foundation::IAsyncOperation<int>^ SshSession::AuthenticatePublicKeyAsync(
                const Platform::Array<uint8>^ privateKeyPem, Platform::String^ passphrase)
            {
                if (privateKeyPem == nullptr)
                {
                    throw ref new Platform::NullReferenceException();
                }
                std::string keyData(reinterpret_cast<const char*>(privateKeyPem->Data),
                                    privateKeyPem->Length);
                const std::string pass = ToUtf8(passphrase);
                SshSession^ self = this;
                return concurrency::create_async(
                    [self, keyData, pass]() -> task<int> {
                        return self->DoAuthenticate(
                            [k = keyData, p = pass](ssh::SshSession* session,
                                                    const ssh::AuthCallback& cb) mutable {
                                std::string pub; // 空：libssh2 从私钥推导公钥
                                return session->authenticatePublicKey(k, pub, p, cb); // 受理即清空
                            });
                    });
            }

            Windows::Foundation::IAsyncOperation<int>^ SshSession::AuthenticateKeyboardInteractiveAsync()
            {
                SshSession^ self = this;
                return concurrency::create_async([self]() -> task<int> {
                    return self->DoAuthenticate([](ssh::SshSession* session, const ssh::AuthCallback& cb) {
                        return session->authenticateKeyboardInteractive(cb);
                    });
                });
            }

            // ---------------------------------------------------------------- shell / exec / 数据

            Windows::Foundation::IAsyncOperation<int>^ SshSession::OpenShellAsync(int cols, int rows)
            {
                SshSession^ self = this;
                return concurrency::create_async([self, cols, rows]() -> task<int> {
                    return self->DoOpenShell(cols, rows);
                });
            }

            task<int> SshSession::DoOpenShell(int cols, int rows)
            {
                if (closed_.load())
                {
                    return concurrency::task_from_result(ssh::kSshErrorCodeInternalError);
                }
                auto tce = std::make_shared<task_completion_event<int>>();
                {
                    std::lock_guard<std::mutex> lock(sessionMutex_);
                    if (session_ == nullptr || session_->state() != ssh::SshSessionState::Established ||
                        shell_ != nullptr)
                    {
                        return concurrency::task_from_result(ssh::kSshErrorCodeInternalError);
                    }
                    Platform::WeakReference weak(this);
                    ssh::SshChannelCallbacks callbacks;
                    callbacks.onOpen = [tce](const ssh::ChannelOpenResult& result) {
                        tce->set(result.success ? 0 : ssh::kSshErrorCodeInternalError);
                    };
                    callbacks.onData = [weak](const std::string& data, ssh::ChannelStream stream) {
                        (void)stream; // 终端只有 stdout 一路渲染
                        SshSession^ self = weak.Resolve<SshSession>();
                        if (self != nullptr)
                        {
                            self->OnShellData(data);
                        }
                    };
                    callbacks.onClose = [weak](const ssh::ChannelCloseInfo& info) {
                        (void)info; // 通道结束不改变会话状态；ChannelClosed 事件属 T03
                        SshSession^ self = weak.Resolve<SshSession>();
                        if (self != nullptr)
                        {
                            self->dirtyCoalescer_.markDirty(); // 唤醒一次拉取，让残量输出被取走
                            self->ContentDirty(self, nullptr);
                        }
                    };
                    auto shell = std::make_unique<ssh::SshChannel>(*session_, callbacks);
                    ssh::PtySpec pty;
                    pty.termType = termType_;
                    pty.cols = static_cast<std::uint32_t>(cols);
                    pty.rows = static_cast<std::uint32_t>(rows);
                    if (!shell->openShell(pty))
                    {
                        return concurrency::task_from_result(ssh::kSshErrorCodeInternalError);
                    }
                    // env 排队在 process_startup 之前发出（被拒仅忽略，N06 语义）
                    for (const auto& pair : envVars_)
                    {
                        shell->setenv(pair.first, pair.second);
                    }
                    shellOpenTce_ = tce;
                    shell_ = std::move(shell);
                    if (screen_ != nullptr)
                    {
                        screen_->ResetGrid(cols, rows);
                    }
                }
                return concurrency::create_task(*tce);
            }

            Windows::Foundation::IAsyncOperation<ExecResult^>^ SshSession::ExecAsync(Platform::String^ command)
            {
                SshSession^ self = this;
                return concurrency::create_async([self, cmd = ToUtf8(command)]() -> ExecResult^ {
                    std::lock_guard<std::mutex> lock(self->sessionMutex_);
                    // 锁持有整个 exec 期间：core 会话与通道非跨线程安全，Shutdown
                    // 释放 session_ 前必须等本调用结束（最坏 30 s 超时）。
                    if (self->closed_.load() || self->session_ == nullptr ||
                        self->session_->state() != ssh::SshSessionState::Established)
                    {
                        return ref new ExecResult(-1, ref new Platform::String(L""),
                                                  ref new Platform::String(L"session not established"));
                    }
                    const ssh::ExecResult result =
                        ssh::execCommand(*self->session_, cmd, kExecTimeoutMs);
                    if (!result.ok)
                    {
                        return ref new ExecResult(-1, ToPlatform(result.stdoutData),
                                                  ToPlatform(result.message.empty()
                                                                 ? result.stderrData
                                                                 : result.message));
                    }
                    return ref new ExecResult(result.exitStatus, ToPlatform(result.stdoutData),
                                              ToPlatform(result.stderrData));
                });
            }

            void SshSession::Write(const Platform::Array<uint8>^ data)
            {
                if (data == nullptr)
                {
                    return;
                }
                std::lock_guard<std::mutex> lock(sessionMutex_);
                if (shell_ == nullptr)
                {
                    return;
                }
                if (!shell_->write(reinterpret_cast<const char*>(data->Data), data->Length))
                {
                    // 背压拒绝（core 4 MiB 队列上限）：整批丢弃。键盘输入量极小，
                    // 触顶说明对端不消费；渲染层流控属 T03。
                }
            }

            void SshSession::Resize(int cols, int rows)
            {
                std::lock_guard<std::mutex> lock(sessionMutex_);
                if (shell_ != nullptr)
                {
                    shell_->resize(static_cast<std::uint32_t>(cols), static_cast<std::uint32_t>(rows));
                }
                if (screen_ != nullptr)
                {
                    screen_->ResetGrid(cols, rows);
                }
            }

            void SshSession::ProbeNow()
            {
                std::lock_guard<std::mutex> lock(sessionMutex_);
                if (session_ != nullptr)
                {
                    session_->probeNow(0); // 0 = core 默认 5 s 判定窗口
                }
            }

            Platform::Array<uint8>^ SshSession::FetchPendingOutput()
            {
                std::string chunk;
                {
                    std::lock_guard<std::mutex> lock(outputMutex_);
                    chunk.swap(pendingOutput_);
                }
                dirtyCoalescer_.markConsumed(); // C# 拉取后复位合并标志
                auto result = ref new Platform::Array<uint8>(static_cast<unsigned int>(chunk.size()));
                if (!chunk.empty())
                {
                    std::memcpy(result->Data, chunk.data(), chunk.size());
                }
                return result;
            }
        }
    }
}
