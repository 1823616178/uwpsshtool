#pragma once

namespace SshTool
{
    namespace Native
    {
        /// <summary>
        /// SP03：验证 libssh2（+ OpenSSL）在 ARM32 UWP 上能否连真实服务器。
        /// 只为回答三个问题：① Winsock 在 AppContainer 里能不能正常连出去；
        /// ② libssh2 握手/认证/exec 能不能跑通；③ 回环 UDP socket 对能否用作事件循环唤醒。
        /// 正式实现见 N02–N09（本类不进生产路径）。
        /// </summary>
        public ref class SshSpike sealed
        {
        public:
            /// <summary>libssh2 版本字符串（证明库链接成功）。</summary>
            static Platform::String^ Version();

            /// <summary>
            /// 连接 → 握手 → 密码认证 → exec(command) → 读 stdout。
            /// 返回一份多行报告（含协商出的 KEX/HOSTKEY/CIPHER/MAC 与耗时），失败时报告里带错误码与阶段。
            /// </summary>
            static Windows::Foundation::IAsyncOperation<Platform::String^>^ ExecAsync(
                Platform::String^ host,
                int port,
                Platform::String^ user,
                Platform::String^ password,
                Platform::String^ command);

            /// <summary>
            /// 要点 3：本进程内回环 UDP socket 对（127.0.0.1 绑定 + 互发 1 字节）。
            /// 这是 EventLoop 跨线程唤醒的备选方案，UWP 下能否用必须实测。
            /// </summary>
            static Platform::String^ LoopbackUdpProbe();
        };
    }
}
