using SshTool.Core.Terminal;
using Xunit;

namespace SshTool.Core.Tests.Terminal
{
    public class ShortcutMapTests
    {
        [Fact]
        public void Default_MatchesSection516()
        {
            ShortcutMap map = ShortcutMap.Default;
            Assert.Empty(map.Conflicts);
            AssertMatch(map, "Ctrl+Shift+T", ShortcutAction.NewTab);
            AssertMatch(map, "Ctrl+Shift+W", ShortcutAction.ClosePane);
            AssertMatch(map, "Ctrl+Tab", ShortcutAction.NextTab);
            AssertMatch(map, "Ctrl+Shift+Tab", ShortcutAction.PrevTab);
            AssertMatch(map, "Ctrl+Shift+D", ShortcutAction.SplitRight);
            AssertMatch(map, "Ctrl+Shift+E", ShortcutAction.SplitDown);
            AssertMatch(map, "Ctrl+Alt+Left", ShortcutAction.FocusLeft);
            AssertMatch(map, "Ctrl+Alt+Up", ShortcutAction.FocusUp);
            AssertMatch(map, "Ctrl+Alt+Right", ShortcutAction.FocusRight);
            AssertMatch(map, "Ctrl+Alt+Down", ShortcutAction.FocusDown);
            AssertMatch(map, "Ctrl+Shift+C", ShortcutAction.Copy);
            AssertMatch(map, "Ctrl+Shift+V", ShortcutAction.Paste);
            AssertMatch(map, "Ctrl+=", ShortcutAction.FontIncrease);
            AssertMatch(map, "Ctrl+-", ShortcutAction.FontDecrease);
            AssertMatch(map, "Ctrl+0", ShortcutAction.FontReset);
            AssertMatch(map, "Ctrl+Shift+F", ShortcutAction.Find);
        }

        [Fact]
        public void Default_DoesNotStealTerminalCtrlC()
        {
            ShortcutAction action;
            Assert.False(ShortcutMap.Default.TryMatch(Parse("Ctrl+C"), out action));
        }

        [Fact]
        public void Parse_EmptyObject_IsDefault()
        {
            Assert.Equal(ShortcutMap.Default.ToJson(), ShortcutMap.Parse("{}").ToJson());
            Assert.Equal(ShortcutMap.Default.ToJson(), ShortcutMap.Parse("").ToJson());
            Assert.Equal(ShortcutMap.Default.ToJson(), ShortcutMap.Parse(null).ToJson());
        }

        [Fact]
        public void Parse_OverridesSingleAction()
        {
            ShortcutMap map = ShortcutMap.Parse("{\"newTab\":\"Ctrl+Alt+T\"}");
            AssertMatch(map, "Ctrl+Alt+T", ShortcutAction.NewTab);
            AssertMatch(map, "Ctrl+Shift+W", ShortcutAction.ClosePane);
            ShortcutAction ignored;
            Assert.False(map.TryMatch(Parse("Ctrl+Shift+T"), out ignored));
        }

        [Fact]
        public void Parse_IgnoresUnknownActionsAndBadChords()
        {
            ShortcutMap map = ShortcutMap.Parse(
                "{\"nope\":\"Ctrl+X\",\"newTab\":\"not-a-chord\",\"copy\":\"Ctrl+Shift+C\"}");
            AssertMatch(map, "Ctrl+Shift+T", ShortcutAction.NewTab);
            AssertMatch(map, "Ctrl+Shift+C", ShortcutAction.Copy);
        }

        [Fact]
        public void Parse_InvalidJson_FallsBackToDefault()
        {
            Assert.Equal(ShortcutMap.Default.ToJson(), ShortcutMap.Parse("{").ToJson());
        }

        [Fact]
        public void ToJson_RoundTrip()
        {
            ShortcutMap original = ShortcutMap.Parse("{\"find\":\"Ctrl+F\",\"copy\":\"Ctrl+Shift+C\"}");
            ShortcutMap roundtrip = ShortcutMap.Parse(original.ToJson());
            Assert.Equal(original.ToJson(), roundtrip.ToJson());
            AssertMatch(roundtrip, "Ctrl+F", ShortcutAction.Find);
        }

        [Fact]
        public void Conflicts_WhenTwoActionsShareChord()
        {
            ShortcutMap map = ShortcutMap.Parse(
                "{\"newTab\":\"Ctrl+Shift+C\",\"copy\":\"Ctrl+Shift+C\"}");
            Assert.NotEmpty(map.Conflicts);
            Assert.Equal(ShortcutAction.NewTab, map.Conflicts[0].First);
            Assert.Equal(ShortcutAction.Copy, map.Conflicts[0].Second);
            ShortcutAction action;
            Assert.True(map.TryMatch(Parse("Ctrl+Shift+C"), out action));
            Assert.Equal(ShortcutAction.NewTab, action);
        }

        [Fact]
        public void Chord_ParsesModifiersAndKeys()
        {
            ShortcutChord tab;
            Assert.True(ShortcutChord.TryParse("ctrl+shift+tab", out tab));
            Assert.True(tab.Ctrl && tab.Shift && !tab.Alt);
            Assert.Equal(TerminalKey.Tab, tab.Key);
            Assert.Equal("Ctrl+Shift+Tab", tab.ToDisplay());
        }

        private static void AssertMatch(ShortcutMap map, string chord, ShortcutAction expected)
        {
            ShortcutAction action;
            Assert.True(map.TryMatch(Parse(chord), out action), chord);
            Assert.Equal(expected, action);
        }

        private static ShortcutChord Parse(string text)
        {
            ShortcutChord chord;
            Assert.True(ShortcutChord.TryParse(text, out chord), text);
            return chord;
        }
    }
}
