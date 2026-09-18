#include "pch.h"
#include "Bridge/ConnectOptions.h"

namespace SshTool
{
    namespace Native
    {
        namespace Bridge
        {
            ConnectOptions::ConnectOptions()
            {
                Port = 22;
                ConnectTimeoutMs = 15000;
                KeepaliveSeconds = 30;
                TermType = L"xterm-256color";
                Cols = 80;
                Rows = 24;
            }
        }
    }
}
