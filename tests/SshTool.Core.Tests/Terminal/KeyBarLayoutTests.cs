using System;
using System.Collections.Generic;
using SshTool.Core.Storage;
using SshTool.Core.Terminal;
using Xunit;

namespace SshTool.Core.Tests.Terminal
{
    public class KeyBarLayoutTests
    {
        [Fact]
        public void DefaultString_MatchesSettingDefinition()
        {
            Assert.Equal(
                KeyBarLayout.DefaultString,
                (string)SettingDefinitions.Find("keyBarLayout").DefaultValue);
        }

        [Fact]
        public void Parse_Default_ContainsExpectedKeys()
        {
            IReadOnlyList<KeyBarKey> keys = KeyBarLayout.Parse(KeyBarLayout.DefaultString);
            Assert.Equal(KeyBarLayout.DefaultString, KeyBarLayout.Serialize(keys));
            Assert.Equal("esc", keys[0].Id);
            Assert.Equal(TerminalKey.Escape, keys[0].TerminalKey);
            Assert.Equal(KeyBarKeyKind.Modifier, Find(keys, "ctrl").Kind);
            Assert.Equal('|', Find(keys, "pipe").Character);
            Assert.Equal(KeyBarAction.Paste, Find(keys, "paste").Action);
            Assert.True(Find(keys, "up").IsArrow);
        }

        [Fact]
        public void Parse_NullOrWhitespace_FallsBackToDefault()
        {
            Assert.Equal(KeyBarLayout.DefaultString, KeyBarLayout.Serialize(KeyBarLayout.Parse(null)));
            Assert.Equal(KeyBarLayout.DefaultString, KeyBarLayout.Serialize(KeyBarLayout.Parse("")));
            Assert.Equal(KeyBarLayout.DefaultString, KeyBarLayout.Serialize(KeyBarLayout.Parse("   ")));
        }

        [Fact]
        public void Parse_IgnoresUnknownIds()
        {
            IReadOnlyList<KeyBarKey> keys = KeyBarLayout.Parse("esc,unknown,nope,tab");
            Assert.Equal("esc,tab", KeyBarLayout.Serialize(keys));
        }

        [Fact]
        public void Parse_DedupesKeepingFirst()
        {
            IReadOnlyList<KeyBarKey> keys = KeyBarLayout.Parse("esc,tab,esc,ctrl,tab");
            Assert.Equal("esc,tab,ctrl", KeyBarLayout.Serialize(keys));
        }

        [Fact]
        public void Parse_IsCaseInsensitiveAndTrims()
        {
            IReadOnlyList<KeyBarKey> keys = KeyBarLayout.Parse(" ESC , Tab ,CTRL ");
            Assert.Equal("esc,tab,ctrl", KeyBarLayout.Serialize(keys));
        }

        [Fact]
        public void Parse_AllUnknown_FallsBackToDefault()
        {
            IReadOnlyList<KeyBarKey> keys = KeyBarLayout.Parse("foo,bar");
            Assert.Equal(KeyBarLayout.DefaultString, KeyBarLayout.Serialize(keys));
        }

        [Fact]
        public void Serialize_RoundTrip_KnownIds()
        {
            string layout = "esc,shift,f12,hidekb,copy,snippets,lbracket,backslash";
            Assert.Equal(layout, KeyBarLayout.Serialize(KeyBarLayout.Parse(layout)));
        }

        [Fact]
        public void Serialize_Ids_SkipsUnknownAndDuplicates()
        {
            string text = KeyBarLayout.Serialize(new[] { "esc", "nope", "esc", "paste" });
            Assert.Equal("esc,paste", text);
        }

        [Fact]
        public void Catalog_ContainsSection56Ids()
        {
            string[] expected =
            {
                "esc", "tab", "ctrl", "alt", "shift",
                "up", "down", "left", "right",
                "home", "end", "pgup", "pgdn", "ins", "del",
                "f1", "f2", "f3", "f4", "f5", "f6",
                "f7", "f8", "f9", "f10", "f11", "f12",
                "pipe", "slash", "backslash", "minus", "underscore", "tilde",
                "colon", "semicolon", "quote", "dquote", "backtick",
                "lt", "gt", "lbrace", "rbrace", "lbracket", "rbracket",
                "paste", "copy", "snippets", "hidekb"
            };
            for (int i = 0; i < expected.Length; i++)
            {
                KeyBarKey key;
                Assert.True(KeyBarLayout.TryGet(expected[i], out key), expected[i]);
            }
            Assert.Equal(expected.Length, KeyBarLayout.KnownIds.Count);
        }

        [Fact]
        public void Symbols_MapToSingleCharacters()
        {
            Assert.Equal('\\', Get("backslash").Character);
            Assert.Equal('~', Get("tilde").Character);
            Assert.Equal('"', Get("dquote").Character);
            Assert.Equal('`', Get("backtick").Character);
            Assert.Equal(KeyBarKeyKind.Character, Get("lbrace").Kind);
        }

        [Fact]
        public void Actions_AreNotTerminalKeys()
        {
            Assert.Equal(KeyBarAction.Copy, Get("copy").Action);
            Assert.Equal(KeyBarAction.Snippets, Get("snippets").Action);
            Assert.Equal(KeyBarAction.HideKeyboard, Get("hidekb").Action);
            Assert.Equal(KeyBarKeyKind.Action, Get("hidekb").Kind);
            Assert.Equal(TerminalKey.None, Get("hidekb").TerminalKey);
        }

        [Fact]
        public void RepeatTiming_MatchesDesign()
        {
            Assert.Equal(400, KeyBarLayout.RepeatInitialMilliseconds);
            Assert.Equal(60, KeyBarLayout.RepeatIntervalMilliseconds);
        }

        private static KeyBarKey Get(string id)
        {
            KeyBarKey key;
            Assert.True(KeyBarLayout.TryGet(id, out key));
            return key;
        }

        private static KeyBarKey Find(IReadOnlyList<KeyBarKey> keys, string id)
        {
            for (int i = 0; i < keys.Count; i++)
            {
                if (keys[i].Id == id)
                {
                    return keys[i];
                }
            }
            throw new InvalidOperationException(id);
        }
    }
}
