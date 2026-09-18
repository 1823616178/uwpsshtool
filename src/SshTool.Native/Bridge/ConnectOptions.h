#pragma once

// N09a：连接参数（01-DESIGN §6.2 草图）。由 C# 侧填充后传入 ConnectAsync。

namespace SshTool
{
    namespace Native
    {
        namespace Bridge
        {
            public ref class ConnectOptions sealed
            {
            public:
                ConnectOptions();

                property Platform::String^ Host;
                property int Port;
                property Platform::String^ Username;
                property int ConnectTimeoutMs;   // 默认 15000
                property int KeepaliveSeconds;   // 默认 30；0 关闭
                property Platform::String^ TermType; // 默认 xterm-256color
                property int Cols;               // 默认 80（OpenShellAsync 默认尺寸）
                property int Rows;               // 默认 24
                // shell 打开后逐个 setenv（被拒仅记日志，见 N06 语义）
                property Windows::Foundation::Collections::IMap<Platform::String^, Platform::String^>^ Env;
                property Platform::String^ JumpSessionId; // F05 跳板预留，N09a 未启用
            };
        }
    }
}
