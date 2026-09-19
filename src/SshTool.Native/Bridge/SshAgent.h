#pragma once

// K03：应用内 Agent 的 WinRT 桥（01-DESIGN.md §6.2；04-TASKS K03）。
//
// 底层是 native/core/ssh/agent（进程内密钥托管：解锁后私钥驻留 native 内存，
// 供多会话复用；惰性超时；挂起时由上层调 LockAll 清除）。
// 模式复用 KeyTool（见 Bridge/KeyTool.h）：
//   - Unlock 经 concurrency::create_async 在后台线程执行；
//   - 任何失败一律返回 false，不抛异常（参数为 null 时抛 NullReferenceException，
//     与 SshSession 现有方法一致，由调用方保证非空）；
//   - 本文件不记录任何日志，调用方只记相位与耗时，绝不记 keyId 以外的敏感内容
//     （日志脱敏：私钥/短语绝不进日志）；
//   - 私钥/短语的本地拷贝用完即清零（crypto::SecureClear 经 shared_ptr 捕获，
//     见 KeyTool.cpp 注释）。
// 线程：core SshAgent 自带互斥锁，多会话并发认证安全；本对象由 C# 层持有单个
// 实例（NativeSshAgent），生命周期覆盖全部会话。

#include "ssh/agent.h"

#include <memory>

namespace SshTool
{
    namespace Native
    {
        namespace Bridge
        {
            public ref class SshAgent sealed
            {
            public:
                SshAgent();

                // 解锁并托管私钥（keyId 采用 KeyEntry id）。privateKey 为私钥文件
                // 全文 UTF-8 字节；passphrase 无则传空串。返回 true = 已托管
                // （调用方 buffer 由 core 受理即清零的副本语义覆盖，本层传入后
                // 仍显式清零本地拷贝）。
                Windows::Foundation::IAsyncOperation<bool>^ UnlockAsync(
                    Platform::String^ keyId,
                    const Platform::Array<uint8>^ privateKey,
                    Platform::String^ passphrase);

                // 手动锁定指定条目；false = 不存在（含已被超时惰性清除）。
                bool Lock(Platform::String^ keyId);

                // 锁定全部（幂等）：超时到期、应用挂起时清除共用此入口。
                void LockAll();

                // 托管超时（分钟，0 = 永不超时；负数按 0 处理）。
                void SetTimeout(int minutes);
                property int TimeoutMinutes { int get(); }

                // keyId 当前是否锁定（不存在/已清除/已超时 → true）。
                bool IsLocked(Platform::String^ keyId);
                property int KeyCount { int get(); }

            internal:
                // 供 SshSession::AuthenticateAgentAsync 取内部实例。
                // 调用方持有本对象引用期间指针有效。
                sshclient::ssh::SshAgent* Core();

            private:
                // pimpl：core 含互斥锁与时钟，不直接作为 ref class 成员。
                std::unique_ptr<sshclient::ssh::SshAgent> agent_;
            };
        }
    }
}
