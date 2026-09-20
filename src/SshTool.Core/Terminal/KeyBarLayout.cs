using System;
using System.Collections.Generic;
using System.Text;

namespace SshTool.Core.Terminal
{
    public enum KeyBarKeyKind
    {
        Function = 0,
        Character = 1,
        Modifier = 2,
        Action = 3
    }

    public enum KeyBarAction
    {
        None = 0,
        Copy = 1,
        Paste = 2,
        Snippets = 3,
        HideKeyboard = 4
    }

    // 02-UI-DESIGN.md §5.6：一条键条键。
    public sealed class KeyBarKey
    {
        internal KeyBarKey(string id, string label, KeyBarKeyKind kind,
                           TerminalKey terminalKey, char character,
                           ModifierKey modifier, KeyBarAction action)
        {
            Id = id;
            Label = label;
            Kind = kind;
            TerminalKey = terminalKey;
            Character = character;
            Modifier = modifier;
            Action = action;
        }

        public string Id { get; private set; }
        public string Label { get; private set; }
        public KeyBarKeyKind Kind { get; private set; }
        public TerminalKey TerminalKey { get; private set; }
        public char Character { get; private set; }
        public ModifierKey Modifier { get; private set; }
        public KeyBarAction Action { get; private set; }

        public bool IsArrow
        {
            get
            {
                return TerminalKey == TerminalKey.Up
                    || TerminalKey == TerminalKey.Down
                    || TerminalKey == TerminalKey.Left
                    || TerminalKey == TerminalKey.Right;
            }
        }
    }

    // 布局字符串解析/序列化：逗号分隔 id，未知忽略、去重（先出现的保留）。
    public static class KeyBarLayout
    {
        public const string DefaultString =
            "esc,tab,ctrl,alt,up,down,left,right,home,end,pgup,pgdn,pipe,slash,minus,tilde,paste";

        public const int RepeatInitialMilliseconds = 400;
        public const int RepeatIntervalMilliseconds = 60;

        private static readonly Dictionary<string, KeyBarKey> Catalog = BuildCatalog();
        private static readonly IReadOnlyList<KeyBarKey> DefaultKeys = Parse(DefaultString);

        public static IReadOnlyList<KeyBarKey> Default
        {
            get { return DefaultKeys; }
        }

        public static IReadOnlyList<string> KnownIds
        {
            get
            {
                var ids = new string[Catalog.Count];
                Catalog.Keys.CopyTo(ids, 0);
                Array.Sort(ids, StringComparer.Ordinal);
                return ids;
            }
        }

        public static bool TryGet(string id, out KeyBarKey key)
        {
            key = null;
            if (string.IsNullOrEmpty(id))
            {
                return false;
            }
            return Catalog.TryGetValue(id.Trim().ToLowerInvariant(), out key);
        }

        public static IReadOnlyList<KeyBarKey> Parse(string layout)
        {
            if (string.IsNullOrWhiteSpace(layout))
            {
                return DefaultKeys ?? ParseCore(DefaultString);
            }
            IReadOnlyList<KeyBarKey> parsed = ParseCore(layout);
            return parsed.Count == 0 ? Default : parsed;
        }

        public static string Serialize(IReadOnlyList<KeyBarKey> keys)
        {
            if (keys == null)
            {
                throw new ArgumentNullException(nameof(keys));
            }
            var sb = new StringBuilder();
            for (int i = 0; i < keys.Count; i++)
            {
                if (keys[i] == null || string.IsNullOrEmpty(keys[i].Id))
                {
                    continue;
                }
                if (sb.Length > 0)
                {
                    sb.Append(',');
                }
                sb.Append(keys[i].Id);
            }
            return sb.ToString();
        }

        public static string Serialize(IReadOnlyList<string> ids)
        {
            if (ids == null)
            {
                throw new ArgumentNullException(nameof(ids));
            }
            var keys = new List<KeyBarKey>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < ids.Count; i++)
            {
                KeyBarKey key;
                if (!TryGet(ids[i], out key) || !seen.Add(key.Id))
                {
                    continue;
                }
                keys.Add(key);
            }
            return Serialize(keys);
        }

        private static IReadOnlyList<KeyBarKey> ParseCore(string layout)
        {
            string[] parts = layout.Split(new[] { ',' }, StringSplitOptions.None);
            var result = new List<KeyBarKey>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < parts.Length; i++)
            {
                KeyBarKey key;
                if (!TryGet(parts[i], out key) || !seen.Add(key.Id))
                {
                    continue;
                }
                result.Add(key);
            }
            return result;
        }

        private static Dictionary<string, KeyBarKey> BuildCatalog()
        {
            var map = new Dictionary<string, KeyBarKey>(StringComparer.Ordinal);
            Add(map, Function("esc", "Esc", TerminalKey.Escape));
            Add(map, Function("tab", "Tab", TerminalKey.Tab));
            Add(map, Modifier("ctrl", "Ctrl", ModifierKey.Ctrl));
            Add(map, Modifier("alt", "Alt", ModifierKey.Alt));
            Add(map, Modifier("shift", "Shift", ModifierKey.Shift));
            Add(map, Function("up", "\u2191", TerminalKey.Up));
            Add(map, Function("down", "\u2193", TerminalKey.Down));
            Add(map, Function("left", "\u2190", TerminalKey.Left));
            Add(map, Function("right", "\u2192", TerminalKey.Right));
            Add(map, Function("home", "Home", TerminalKey.Home));
            Add(map, Function("end", "End", TerminalKey.End));
            Add(map, Function("pgup", "PgUp", TerminalKey.PageUp));
            Add(map, Function("pgdn", "PgDn", TerminalKey.PageDown));
            Add(map, Function("ins", "Ins", TerminalKey.Insert));
            Add(map, Function("del", "Del", TerminalKey.Delete));
            Add(map, Function("f1", "F1", TerminalKey.F1));
            Add(map, Function("f2", "F2", TerminalKey.F2));
            Add(map, Function("f3", "F3", TerminalKey.F3));
            Add(map, Function("f4", "F4", TerminalKey.F4));
            Add(map, Function("f5", "F5", TerminalKey.F5));
            Add(map, Function("f6", "F6", TerminalKey.F6));
            Add(map, Function("f7", "F7", TerminalKey.F7));
            Add(map, Function("f8", "F8", TerminalKey.F8));
            Add(map, Function("f9", "F9", TerminalKey.F9));
            Add(map, Function("f10", "F10", TerminalKey.F10));
            Add(map, Function("f11", "F11", TerminalKey.F11));
            Add(map, Function("f12", "F12", TerminalKey.F12));
            Add(map, Character("pipe", "|", '|'));
            Add(map, Character("slash", "/", '/'));
            Add(map, Character("backslash", "\\", '\\'));
            Add(map, Character("minus", "-", '-'));
            Add(map, Character("underscore", "_", '_'));
            Add(map, Character("tilde", "~", '~'));
            Add(map, Character("colon", ":", ':'));
            Add(map, Character("semicolon", ";", ';'));
            Add(map, Character("quote", "'", '\''));
            Add(map, Character("dquote", "\"", '"'));
            Add(map, Character("backtick", "`", '`'));
            Add(map, Character("lt", "<", '<'));
            Add(map, Character("gt", ">", '>'));
            Add(map, Character("lbrace", "{", '{'));
            Add(map, Character("rbrace", "}", '}'));
            Add(map, Character("lbracket", "[", '['));
            Add(map, Character("rbracket", "]", ']'));
            Add(map, Action("paste", "\u7C98\u8D34", KeyBarAction.Paste));
            Add(map, Action("copy", "\u590D\u5236", KeyBarAction.Copy));
            Add(map, Action("snippets", "\u7247\u6BB5", KeyBarAction.Snippets));
            // V01a 评审回补：hidekb 键面是 App 层 IconKeyboard 字形按钮（终端页固定按钮），
            // Core 不出文案；编辑器行首改显示 resw 语义文本（KeyBarLayoutEditorPage.DisplayLabel）。
            Add(map, Action("hidekb", string.Empty, KeyBarAction.HideKeyboard));
            return map;
        }

        private static void Add(Dictionary<string, KeyBarKey> map, KeyBarKey key)
        {
            map[key.Id] = key;
        }

        private static KeyBarKey Function(string id, string label, TerminalKey terminalKey)
        {
            return new KeyBarKey(id, label, KeyBarKeyKind.Function, terminalKey, '\0', ModifierKey.Ctrl, KeyBarAction.None);
        }

        private static KeyBarKey Character(string id, string label, char character)
        {
            return new KeyBarKey(id, label, KeyBarKeyKind.Character, TerminalKey.Char, character, ModifierKey.Ctrl, KeyBarAction.None);
        }

        private static KeyBarKey Modifier(string id, string label, ModifierKey modifier)
        {
            return new KeyBarKey(id, label, KeyBarKeyKind.Modifier, TerminalKey.None, '\0', modifier, KeyBarAction.None);
        }

        private static KeyBarKey Action(string id, string label, KeyBarAction action)
        {
            return new KeyBarKey(id, label, KeyBarKeyKind.Action, TerminalKey.None, '\0', ModifierKey.Ctrl, action);
        }
    }

    public sealed class KeyBarActionEventArgs : EventArgs
    {
        public KeyBarActionEventArgs(KeyBarAction action)
        {
            Action = action;
        }

        public KeyBarAction Action { get; private set; }
    }
}
