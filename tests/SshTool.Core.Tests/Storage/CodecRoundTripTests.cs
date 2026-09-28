using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SshTool.Core.Models;
using SshTool.Core.Storage.Codecs;
using Xunit;

namespace SshTool.Core.Tests.Storage
{
    // D02 验收：7 个编解码器往返单测（字段值 + Extra 未知字段 + 键序稳定）。
    public class CodecRoundTripTests
    {
        [Fact]
        public void Host_RoundTrip()
        {
            var original = Defaults.NewHost();
            original.Name = "生产机";
            original.HostName = "prod.example.com";
            original.Port = 2222;
            original.Username = "deploy";
            original.AuthType = AuthType.Key;
            original.HostFingerprint = "SHA256:abc";
            original.Keepalive = 60;
            original.GroupId = "g1";
            original.KeyId = "k1";
            original.AppearanceId = "a1";
            original.JumpHostId = "j1";
            original.InitCommands.Add("tmux ls");
            original.InitCommands.Add("htop");
            original.EnvVars["EDITOR"] = "vim";
            original.TmuxAutoAttach = true;
            original.TmuxSessionName = "work";
            original.BackspaceSendsCtrlH = true;
            original.SortOrder = 7;
            original.LastConnectedAt = "2026-09-17T10:00:00.000Z";
            original.Favorite = true;
            original.Extra = new JObject
            {
                ["futureField"] = new JValue(42),
                ["futureObj"] = new JObject { ["x"] = new JValue(true) }
            };

            RoundTrip(new HostCodec(), original, (a, b) =>
            {
                Assert.Equal(a.Id, b.Id);
                Assert.Equal(a.Name, b.Name);
                Assert.Equal(a.HostName, b.HostName);
                Assert.Equal(a.Port, b.Port);
                Assert.Equal(a.Username, b.Username);
                Assert.Equal(a.AuthType, b.AuthType);
                Assert.Equal(a.HostFingerprint, b.HostFingerprint);
                Assert.Equal(a.Keepalive, b.Keepalive);
                Assert.Equal(a.GroupId, b.GroupId);
                Assert.Equal(a.KeyId, b.KeyId);
                Assert.Equal(a.AppearanceId, b.AppearanceId);
                Assert.Equal(a.JumpHostId, b.JumpHostId);
                Assert.Equal(a.InitCommands, b.InitCommands);
                Assert.Equal(a.EnvVars, b.EnvVars);
                Assert.Equal(a.TermType, b.TermType);
                Assert.Equal(a.TmuxAutoAttach, b.TmuxAutoAttach);
                Assert.Equal(a.TmuxSessionName, b.TmuxSessionName);
                Assert.Equal(a.BackspaceSendsCtrlH, b.BackspaceSendsCtrlH);
                Assert.Equal(a.SortOrder, b.SortOrder);
                Assert.Equal(a.LastConnectedAt, b.LastConnectedAt);
                Assert.Equal(a.Favorite, b.Favorite);
                AssertExtraEqual(a, b);
            });
        }

        [Fact]
        public void HostGroup_RoundTrip()
        {
            var original = Defaults.NewGroup("工作");
            original.Color = "#A1B2C3";
            original.Order = 3;
            original.Collapsed = true;
            original.Extra = new JObject { ["future"] = new JValue("x") };

            RoundTrip(new HostGroupCodec(), original, (a, b) =>
            {
                Assert.Equal(a.Id, b.Id);
                Assert.Equal(a.Name, b.Name);
                Assert.Equal(a.Color, b.Color);
                Assert.Equal(a.Order, b.Order);
                Assert.Equal(a.Collapsed, b.Collapsed);
                AssertExtraEqual(a, b);
            });
        }

        [Fact]
        public void Tunnel_RoundTrip()
        {
            var original = Defaults.NewTunnel("srv-1");
            original.Name = "中转";
            original.GroupId = "g1";
            original.Type = TunnelType.Relay;
            original.ListenHost = "0.0.0.0";
            original.ListenPort = 8080;
            original.DestHost = "internal.lan";
            original.DestPort = 443;
            original.DestServerId = "srv-2";
            original.AutoReconnect = false;
            original.Enabled = false;
            original.AutoStart = true;
            original.Extra = new JObject { ["future"] = new JArray(1, 2) };

            RoundTrip(new TunnelCodec(), original, (a, b) =>
            {
                Assert.Equal(a.Id, b.Id);
                Assert.Equal(a.Name, b.Name);
                Assert.Equal(a.ServerId, b.ServerId);
                Assert.Equal(a.GroupId, b.GroupId);
                Assert.Equal(a.Type, b.Type);
                Assert.Equal(a.ListenHost, b.ListenHost);
                Assert.Equal(a.ListenPort, b.ListenPort);
                Assert.Equal(a.DestHost, b.DestHost);
                Assert.Equal(a.DestPort, b.DestPort);
                Assert.Equal(a.DestServerId, b.DestServerId);
                Assert.Equal(a.AutoReconnect, b.AutoReconnect);
                Assert.Equal(a.Enabled, b.Enabled);
                Assert.Equal(a.AutoStart, b.AutoStart);
                AssertExtraEqual(a, b);
            });
        }

        [Fact]
        public void KeyEntry_RoundTrip()
        {
            var original = new KeyEntry
            {
                Id = "k1",
                Name = "主密钥",
                KeyType = "ssh-ed25519",
                Bits = 256,
                Format = "openssh",
                Encrypted = true,
                PublicKeyOpenSsh = "ssh-ed25519 AAAA...",
                FingerprintSha256 = "SHA256:xyz",
                CreatedAt = "2026-09-17T10:00:00.000Z",
                Comment = "测试",
                Extra = new JObject { ["future"] = new JValue(false) }
            };

            RoundTrip(new KeyEntryCodec(), original, (a, b) =>
            {
                Assert.Equal(a.Id, b.Id);
                Assert.Equal(a.Name, b.Name);
                Assert.Equal(a.KeyType, b.KeyType);
                Assert.Equal(a.Bits, b.Bits);
                Assert.Equal(a.Format, b.Format);
                Assert.Equal(a.Encrypted, b.Encrypted);
                Assert.Equal(a.PublicKeyOpenSsh, b.PublicKeyOpenSsh);
                Assert.Equal(a.FingerprintSha256, b.FingerprintSha256);
                Assert.Equal(a.CreatedAt, b.CreatedAt);
                Assert.Equal(a.Comment, b.Comment);
                AssertExtraEqual(a, b);
            });
        }

        [Fact]
        public void Snippet_RoundTrip()
        {
            var original = Defaults.NewSnippet();
            original.Name = "重启 nginx";
            original.Content = "sudo systemctl restart nginx";
            original.GroupName = "运维";
            original.SortOrder = 5;
            original.SendEnter = false;
            original.Extra = new JObject { ["future"] = new JValue(1.5) };

            RoundTrip(new SnippetCodec(), original, (a, b) =>
            {
                Assert.Equal(a.Id, b.Id);
                Assert.Equal(a.Name, b.Name);
                Assert.Equal(a.Content, b.Content);
                Assert.Equal(a.GroupName, b.GroupName);
                Assert.Equal(a.SortOrder, b.SortOrder);
                Assert.Equal(a.SendEnter, b.SendEnter);
                AssertExtraEqual(a, b);
            });
        }

        [Fact]
        public void Appearance_RoundTrip()
        {
            var original = Defaults.DefaultAppearance();
            original.CursorStyle = CursorStyle.Bar;
            original.LineHeight = 1.35;
            original.Extra = new JObject { ["future"] = new JValue("y") };

            RoundTrip(new AppearanceCodec(), original, (a, b) =>
            {
                Assert.Equal(a.Id, b.Id);
                Assert.Equal(a.Name, b.Name);
                Assert.Equal(a.BuiltIn, b.BuiltIn);
                Assert.Equal(a.FontFamily, b.FontFamily);
                Assert.Equal(a.FontSize, b.FontSize);
                Assert.Equal(a.LineHeight, b.LineHeight);
                Assert.Equal(a.FontWeightBold, b.FontWeightBold);
                Assert.Equal(a.BoldAsBright, b.BoldAsBright);
                Assert.Equal(a.CursorStyle, b.CursorStyle);
                Assert.Equal(a.CursorBlink, b.CursorBlink);
                Assert.Equal(a.Padding, b.Padding);
                Assert.Equal(a.Palette, b.Palette);
                Assert.Equal(a.Foreground, b.Foreground);
                Assert.Equal(a.Background, b.Background);
                Assert.Equal(a.Cursor, b.Cursor);
                Assert.Equal(a.Selection, b.Selection);
                AssertExtraEqual(a, b);
            });
        }

        [Fact]
        public void KnownHost_RoundTrip()
        {
            var original = new KnownHost
            {
                Id = "h1",
                Host = "example.com",
                Port = 22,
                KeyType = "ssh-ed25519",
                FingerprintSha256 = "SHA256:zzz",
                AddedAt = "2026-09-17T10:00:00.000Z",
                LastSeenAt = "2026-09-17T11:00:00.000Z",
                Extra = new JObject { ["future"] = new JValue(0) }
            };

            RoundTrip(new KnownHostCodec(), original, (a, b) =>
            {
                Assert.Equal(a.Id, b.Id);
                Assert.Equal(a.Host, b.Host);
                Assert.Equal(a.Port, b.Port);
                Assert.Equal(a.KeyType, b.KeyType);
                Assert.Equal(a.FingerprintSha256, b.FingerprintSha256);
                Assert.Equal(a.AddedAt, b.AddedAt);
                Assert.Equal(a.LastSeenAt, b.LastSeenAt);
                AssertExtraEqual(a, b);
            });
        }

        [Fact]
        public void Decode_MinimalJson_UsesDefaultsAndNullExtra()
        {
            var host = new HostCodec().Decode(new JObject { ["id"] = new JValue("x") });
            Assert.Equal("x", host.Id);
            Assert.Equal(0, host.Port);
            Assert.Equal(AuthType.Password, host.AuthType);
            Assert.Empty(host.InitCommands);
            Assert.Empty(host.EnvVars);
            Assert.Null(host.Extra);
        }

        [Theory]
        [InlineData("bogus")]
        [InlineData("PASSWORD")]
        public void Host_UnknownAuthType_Throws(string value)
        {
            var json = new JObject { ["authType"] = new JValue(value) };
            Assert.Throws<JsonException>(() => new HostCodec().Decode(json));
        }

        [Fact]
        public void Tunnel_UnknownType_Throws()
        {
            var json = new JObject { ["type"] = new JValue("wormhole") };
            Assert.Throws<JsonException>(() => new TunnelCodec().Decode(json));
        }

        [Fact]
        public void Appearance_UnknownCursorStyle_Throws()
        {
            var json = new JObject { ["cursorStyle"] = new JValue("beam") };
            Assert.Throws<JsonException>(() => new AppearanceCodec().Decode(json));
        }

        [Fact]
        public void Host_WrongFieldType_Throws()
        {
            var json = new JObject { ["port"] = new JValue("22") };
            Assert.Throws<JsonException>(() => new HostCodec().Decode(json));
        }

        [Fact]
        public void Extra_CollidingWithKnownKey_KnownFieldWins()
        {
            var host = Defaults.NewHost();
            host.Name = "真实名称";
            host.Extra = new JObject { ["name"] = new JValue("冒充者") };
            var json = new HostCodec().Encode(host);
            Assert.Equal("真实名称", (string)json["name"]);
        }

        private static void RoundTrip<T>(SshTool.Core.Storage.IEntityCodec<T> codec, T original, System.Action<T, T> assertEqual)
        {
            string json = codec.Encode(original).ToString(Formatting.None);
            var decoded = codec.Decode(SshTool.Core.Storage.JsonText.ParseObject(json));
            assertEqual(original, decoded);
            // 再编码结果必须逐字节一致（键序 + Extra 往返稳定）
            string json2 = codec.Encode(decoded).ToString(Formatting.None);
            Assert.Equal(json, json2);
        }

        private static void AssertExtraEqual(Host a, Host b) { Assert.Equal(JTokenString(a.Extra), JTokenString(b.Extra)); }
        private static void AssertExtraEqual(HostGroup a, HostGroup b) { Assert.Equal(JTokenString(a.Extra), JTokenString(b.Extra)); }
        private static void AssertExtraEqual(Tunnel a, Tunnel b) { Assert.Equal(JTokenString(a.Extra), JTokenString(b.Extra)); }
        private static void AssertExtraEqual(KeyEntry a, KeyEntry b) { Assert.Equal(JTokenString(a.Extra), JTokenString(b.Extra)); }
        private static void AssertExtraEqual(Snippet a, Snippet b) { Assert.Equal(JTokenString(a.Extra), JTokenString(b.Extra)); }
        private static void AssertExtraEqual(AppearanceProfile a, AppearanceProfile b) { Assert.Equal(JTokenString(a.Extra), JTokenString(b.Extra)); }
        private static void AssertExtraEqual(KnownHost a, KnownHost b) { Assert.Equal(JTokenString(a.Extra), JTokenString(b.Extra)); }

        private static string JTokenString(JObject extra)
        {
            return extra == null ? null : extra.ToString(Formatting.None);
        }
    }
}
