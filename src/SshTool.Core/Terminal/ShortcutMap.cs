using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using SshTool.Core.Storage;

namespace SshTool.Core.Terminal
{
    // 02-UI-DESIGN.md §5.16：应用快捷动作（默认组合都带 Shift，避免抢 Ctrl+C）。
    public enum ShortcutAction
    {
        NewTab = 0,
        ClosePane = 1,
        NextTab = 2,
        PrevTab = 3,
        SplitRight = 4,
        SplitDown = 5,
        FocusLeft = 6,
        FocusUp = 7,
        FocusRight = 8,
        FocusDown = 9,
        Copy = 10,
        Paste = 11,
        FontIncrease = 12,
        FontDecrease = 13,
        FontReset = 14,
        Find = 15
    }

    public struct ShortcutChord : IEquatable<ShortcutChord>
    {
        public ShortcutChord(bool ctrl, bool alt, bool shift, TerminalKey key, char character)
        {
            Ctrl = ctrl;
            Alt = alt;
            Shift = shift;
            Key = key;
            Character = NormalizeChar(character);
        }

        public bool Ctrl { get; }
        public bool Alt { get; }
        public bool Shift { get; }
        public TerminalKey Key { get; }
        public char Character { get; }

        public bool Equals(ShortcutChord other)
        {
            return Ctrl == other.Ctrl && Alt == other.Alt && Shift == other.Shift
                && Key == other.Key && Character == other.Character;
        }

        public override bool Equals(object obj)
        {
            return obj is ShortcutChord && Equals((ShortcutChord)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Ctrl ? 1 : 0;
                hash = (hash * 397) ^ (Alt ? 1 : 0);
                hash = (hash * 397) ^ (Shift ? 1 : 0);
                hash = (hash * 397) ^ (int)Key;
                hash = (hash * 397) ^ Character;
                return hash;
            }
        }

        public string ToDisplay()
        {
            var sb = new StringBuilder();
            if (Ctrl) { sb.Append("Ctrl+"); }
            if (Alt) { sb.Append("Alt+"); }
            if (Shift) { sb.Append("Shift+"); }
            sb.Append(KeyToken(Key, Character));
            return sb.ToString();
        }

        public override string ToString()
        {
            return ToDisplay();
        }

        public static bool TryParse(string text, out ShortcutChord chord)
        {
            chord = default(ShortcutChord);
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }
            string[] parts = text.Split(new[] { '+' }, StringSplitOptions.None);
            if (parts.Length == 0)
            {
                return false;
            }
            bool ctrl = false, alt = false, shift = false;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string token = parts[i].Trim();
                if (token.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)
                    || token.Equals("Control", StringComparison.OrdinalIgnoreCase))
                {
                    ctrl = true;
                }
                else if (token.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                {
                    alt = true;
                }
                else if (token.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                {
                    shift = true;
                }
                else
                {
                    return false;
                }
            }
            TerminalKey key;
            char character;
            if (!TryParseKey(parts[parts.Length - 1].Trim(), out key, out character))
            {
                return false;
            }
            chord = new ShortcutChord(ctrl, alt, shift, key, character);
            return true;
        }

        private static char NormalizeChar(char c)
        {
            if (c >= 'A' && c <= 'Z')
            {
                return (char)(c + 32);
            }
            return c;
        }

        private static string KeyToken(TerminalKey key, char character)
        {
            if (key == TerminalKey.Char)
            {
                if (character == '=') { return "="; }
                if (character == '-') { return "-"; }
                return character.ToString().ToUpperInvariant();
            }
            switch (key)
            {
                case TerminalKey.Tab: return "Tab";
                case TerminalKey.Left: return "Left";
                case TerminalKey.Right: return "Right";
                case TerminalKey.Up: return "Up";
                case TerminalKey.Down: return "Down";
                case TerminalKey.Enter: return "Enter";
                case TerminalKey.Escape: return "Esc";
                case TerminalKey.Backspace: return "Backspace";
                case TerminalKey.Home: return "Home";
                case TerminalKey.End: return "End";
                case TerminalKey.PageUp: return "PageUp";
                case TerminalKey.PageDown: return "PageDown";
                case TerminalKey.Insert: return "Insert";
                case TerminalKey.Delete: return "Delete";
                case TerminalKey.F1: return "F1";
                case TerminalKey.F2: return "F2";
                case TerminalKey.F3: return "F3";
                case TerminalKey.F4: return "F4";
                case TerminalKey.F5: return "F5";
                case TerminalKey.F6: return "F6";
                case TerminalKey.F7: return "F7";
                case TerminalKey.F8: return "F8";
                case TerminalKey.F9: return "F9";
                case TerminalKey.F10: return "F10";
                case TerminalKey.F11: return "F11";
                case TerminalKey.F12: return "F12";
                default: return key.ToString();
            }
        }

        private static bool TryParseKey(string token, out TerminalKey key, out char character)
        {
            key = TerminalKey.None;
            character = '\0';
            if (string.IsNullOrEmpty(token))
            {
                return false;
            }
            if (token.Length == 1)
            {
                char c = token[0];
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                    || (c >= '0' && c <= '9') || c == '=' || c == '-' || c == '+')
                {
                    key = TerminalKey.Char;
                    character = c == '+' ? '=' : c;
                    return true;
                }
            }
            switch (token.ToUpperInvariant())
            {
                case "TAB": key = TerminalKey.Tab; return true;
                case "LEFT": key = TerminalKey.Left; return true;
                case "RIGHT": key = TerminalKey.Right; return true;
                case "UP": key = TerminalKey.Up; return true;
                case "DOWN": key = TerminalKey.Down; return true;
                case "ENTER": key = TerminalKey.Enter; return true;
                case "ESC":
                case "ESCAPE": key = TerminalKey.Escape; return true;
                case "BACKSPACE": key = TerminalKey.Backspace; return true;
                case "HOME": key = TerminalKey.Home; return true;
                case "END": key = TerminalKey.End; return true;
                case "PAGEUP":
                case "PGUP": key = TerminalKey.PageUp; return true;
                case "PAGEDOWN":
                case "PGDN": key = TerminalKey.PageDown; return true;
                case "INSERT":
                case "INS": key = TerminalKey.Insert; return true;
                case "DELETE":
                case "DEL": key = TerminalKey.Delete; return true;
                case "EQUALS":
                case "PLUS":
                    key = TerminalKey.Char; character = '='; return true;
                case "MINUS":
                    key = TerminalKey.Char; character = '-'; return true;
                default:
                    return false;
            }
        }
    }

    public sealed class ShortcutBinding
    {
        public ShortcutBinding(ShortcutAction action, ShortcutChord chord)
        {
            Action = action;
            Chord = chord;
        }

        public ShortcutAction Action { get; private set; }
        public ShortcutChord Chord { get; private set; }
    }

    public sealed class ShortcutConflict
    {
        public ShortcutConflict(ShortcutChord chord, ShortcutAction first, ShortcutAction second)
        {
            Chord = chord;
            First = first;
            Second = second;
        }

        public ShortcutChord Chord { get; private set; }
        public ShortcutAction First { get; private set; }
        public ShortcutAction Second { get; private set; }
    }

    public sealed class ShortcutActionEventArgs : EventArgs
    {
        public ShortcutActionEventArgs(ShortcutAction action)
        {
            Action = action;
        }

        public ShortcutAction Action { get; private set; }
    }

    // 快捷键表：默认 §5.16；JSON 对象按动作名覆盖；"{}" 即默认。
    public sealed class ShortcutMap
    {
        private static readonly string[] ActionNames =
        {
            "newTab", "closePane", "nextTab", "prevTab",
            "splitRight", "splitDown",
            "focusLeft", "focusUp", "focusRight", "focusDown",
            "copy", "paste",
            "fontIncrease", "fontDecrease", "fontReset", "find"
        };

        private readonly ShortcutBinding[] _bindings;
        private readonly ShortcutConflict[] _conflicts;

        private ShortcutMap(ShortcutBinding[] bindings)
        {
            _bindings = bindings;
            _conflicts = DetectConflicts(bindings);
        }

        public IReadOnlyList<ShortcutBinding> Bindings
        {
            get { return _bindings; }
        }

        public IReadOnlyList<ShortcutConflict> Conflicts
        {
            get { return _conflicts; }
        }

        public static ShortcutMap Default { get; } = FromDefaults();

        public static ShortcutMap Parse(string json)
        {
            Dictionary<ShortcutAction, ShortcutChord> map = DefaultDictionary();
            if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}")
            {
                return FromDictionary(map);
            }
            JObject obj;
            try
            {
                obj = JsonText.ParseObject(json);
            }
            catch (Exception)
            {
                return Default;
            }
            foreach (JProperty property in obj.Properties())
            {
                ShortcutAction action;
                if (!TryParseAction(property.Name, out action))
                {
                    continue;
                }
                string value = property.Value != null && property.Value.Type == JTokenType.String
                    ? (string)property.Value : null;
                ShortcutChord chord;
                if (ShortcutChord.TryParse(value, out chord))
                {
                    map[action] = chord;
                }
            }
            return FromDictionary(map);
        }

        public string ToJson()
        {
            var obj = new JObject();
            for (int i = 0; i < _bindings.Length; i++)
            {
                ShortcutBinding binding = _bindings[i];
                obj[NameOf(binding.Action)] = binding.Chord.ToDisplay();
            }
            return obj.ToString(Newtonsoft.Json.Formatting.None);
        }

        public bool TryMatch(ShortcutChord chord, out ShortcutAction action)
        {
            for (int i = 0; i < _bindings.Length; i++)
            {
                if (_bindings[i].Chord.Equals(chord))
                {
                    action = _bindings[i].Action;
                    return true;
                }
            }
            action = ShortcutAction.NewTab;
            return false;
        }

        public bool TryMatch(bool ctrl, bool alt, bool shift, TerminalKey key, char character,
                             out ShortcutAction action)
        {
            return TryMatch(new ShortcutChord(ctrl, alt, shift, key, character), out action);
        }

        private static ShortcutMap FromDefaults()
        {
            return FromDictionary(DefaultDictionary());
        }

        private static ShortcutMap FromDictionary(Dictionary<ShortcutAction, ShortcutChord> map)
        {
            var bindings = new ShortcutBinding[ActionNames.Length];
            for (int i = 0; i < ActionNames.Length; i++)
            {
                var action = (ShortcutAction)i;
                bindings[i] = new ShortcutBinding(action, map[action]);
            }
            return new ShortcutMap(bindings);
        }

        private static Dictionary<ShortcutAction, ShortcutChord> DefaultDictionary()
        {
            var map = new Dictionary<ShortcutAction, ShortcutChord>();
            map[ShortcutAction.NewTab] = ParseRequired("Ctrl+Shift+T");
            map[ShortcutAction.ClosePane] = ParseRequired("Ctrl+Shift+W");
            map[ShortcutAction.NextTab] = ParseRequired("Ctrl+Tab");
            map[ShortcutAction.PrevTab] = ParseRequired("Ctrl+Shift+Tab");
            map[ShortcutAction.SplitRight] = ParseRequired("Ctrl+Shift+D");
            map[ShortcutAction.SplitDown] = ParseRequired("Ctrl+Shift+E");
            map[ShortcutAction.FocusLeft] = ParseRequired("Ctrl+Alt+Left");
            map[ShortcutAction.FocusUp] = ParseRequired("Ctrl+Alt+Up");
            map[ShortcutAction.FocusRight] = ParseRequired("Ctrl+Alt+Right");
            map[ShortcutAction.FocusDown] = ParseRequired("Ctrl+Alt+Down");
            map[ShortcutAction.Copy] = ParseRequired("Ctrl+Shift+C");
            map[ShortcutAction.Paste] = ParseRequired("Ctrl+Shift+V");
            map[ShortcutAction.FontIncrease] = ParseRequired("Ctrl+=");
            map[ShortcutAction.FontDecrease] = ParseRequired("Ctrl+-");
            map[ShortcutAction.FontReset] = ParseRequired("Ctrl+0");
            map[ShortcutAction.Find] = ParseRequired("Ctrl+Shift+F");
            return map;
        }

        private static ShortcutChord ParseRequired(string text)
        {
            ShortcutChord chord;
            if (!ShortcutChord.TryParse(text, out chord))
            {
                throw new InvalidOperationException("默认快捷键无法解析: " + text);
            }
            return chord;
        }

        private static bool TryParseAction(string name, out ShortcutAction action)
        {
            action = ShortcutAction.NewTab;
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            for (int i = 0; i < ActionNames.Length; i++)
            {
                if (ActionNames[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    action = (ShortcutAction)i;
                    return true;
                }
            }
            return false;
        }

        private static string NameOf(ShortcutAction action)
        {
            int index = (int)action;
            if (index < 0 || index >= ActionNames.Length)
            {
                return action.ToString();
            }
            return ActionNames[index];
        }

        private static ShortcutConflict[] DetectConflicts(ShortcutBinding[] bindings)
        {
            var found = new List<ShortcutConflict>();
            for (int i = 0; i < bindings.Length; i++)
            {
                for (int j = i + 1; j < bindings.Length; j++)
                {
                    if (bindings[i].Chord.Equals(bindings[j].Chord))
                    {
                        found.Add(new ShortcutConflict(bindings[i].Chord, bindings[i].Action, bindings[j].Action));
                    }
                }
            }
            return found.ToArray();
        }
    }
}
