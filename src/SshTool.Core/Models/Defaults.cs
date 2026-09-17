using System;
using System.Collections.Generic;
using SshTool.Core.Models;

namespace SshTool.Core.Models
{
    // D01：各实体默认值工厂（01-DESIGN.md §8.1 默认值列）。
    public static class Defaults
    {
        public static Host NewHost()
        {
            return new Host
            {
                Id = Common.IdGenerator.NewId(),
                Name = "",
                HostName = "",
                Port = 22,
                Username = "",
                AuthType = AuthType.Password,
                HostFingerprint = "",
                Keepalive = 30,
                TermType = "xterm-256color",
                TmuxAutoAttach = false,
                TmuxSessionName = "",
                BackspaceSendsCtrlH = false,
                SortOrder = 0
            };
        }

        public static HostGroup NewGroup(string name)
        {
            return new HostGroup
            {
                Id = Common.IdGenerator.NewId(),
                Name = name ?? "",
                Color = "#4F8CFF",
                Order = 0,
                Collapsed = false
            };
        }

        public static Tunnel NewTunnel(string serverId)
        {
            return new Tunnel
            {
                Id = Common.IdGenerator.NewId(),
                Name = "",
                ServerId = serverId,
                Type = TunnelType.Local,
                ListenHost = "127.0.0.1",
                ListenPort = 8080,
                DestHost = "",
                DestPort = 0,
                AutoReconnect = true,
                Enabled = true,
                AutoStart = false
            };
        }

        public static Snippet NewSnippet()
        {
            return new Snippet
            {
                Id = Common.IdGenerator.NewId(),
                Name = "",
                Content = "",
                GroupName = "",
                SortOrder = 0,
                SendEnter = true
            };
        }

        // 默认外观：随包 JetBrains Mono（§7.4），配色取 AMOLED 友好深色
        public static AppearanceProfile DefaultAppearance()
        {
            return new AppearanceProfile
            {
                Id = "builtin-default",
                Name = "默认深色",
                BuiltIn = true,
                FontFamily = "JetBrains Mono",
                FontSize = 12,
                LineHeight = 1.2,
                FontWeightBold = false,
                BoldAsBright = true,
                CursorStyle = CursorStyle.Block,
                CursorBlink = true,
                Padding = 4,
                Palette = new List<string>
                {
                    "#000000", "#CC0403", "#19CB00", "#CECB00",
                    "#0D73CC", "#CB1ED1", "#0DCDCD", "#DDDDDD",
                    "#767676", "#F2201F", "#23FD00", "#FAFD00",
                    "#1A8FFF", "#FD28FF", "#14FFFF", "#FFFFFF"
                },
                Foreground = "#E8EEFB",
                Background = "#000000",
                Cursor = "#E8EEFB",
                Selection = "#3A4A66"
            };
        }
    }
}
