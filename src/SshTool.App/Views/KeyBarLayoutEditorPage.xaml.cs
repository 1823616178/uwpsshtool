using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SshTool.App.Infrastructure;
using SshTool.Core.Storage;
using SshTool.Core.Terminal;
using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Views
{
    public sealed class KeyBarKeyItem
    {
        public string Id { get; set; }
        public string Label { get; set; }

        // 走查 S 组：副行显示中文名称，不再把 "pgup"/"pipe" 这类内部 id 当文案。
        public string Description { get; set; }
    }

    // 内置键条键的显示名（KeyBarKey 只有 Id + 键面 Label，没有 Name/DisplayName）。
    // 与 KeyBar 控件的无障碍名共用一套 resw 键（KeyBar_Key_*，Q05 口径），编辑器副行
    // 不再自备一套中文。未收录的 id 统一退化为「自定义按键」，绝不回落到 id。
    public static class KeyBarKeyNames
    {
        // id → KeyBar_Key_<Suffix>。新按键先在 resw 双语补键，再在这里登记映射。
        private static readonly Dictionary<string, string> ResourceSuffixes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "esc", "Esc" },
            { "tab", "Tab" },
            { "ctrl", "Ctrl" },
            { "alt", "Alt" },
            { "shift", "Shift" },
            { "up", "Up" },
            { "down", "Down" },
            { "left", "Left" },
            { "right", "Right" },
            { "home", "Home" },
            { "end", "End" },
            { "pgup", "PgUp" },
            { "pgdn", "PgDn" },
            { "ins", "Ins" },
            { "del", "Del" },
            { "pipe", "Pipe" },
            { "slash", "Slash" },
            { "backslash", "Backslash" },
            { "minus", "Minus" },
            { "underscore", "Underscore" },
            { "tilde", "Tilde" },
            { "colon", "Colon" },
            { "semicolon", "Semicolon" },
            { "quote", "SingleQuote" },
            { "dquote", "DoubleQuote" },
            { "backtick", "Backtick" },
            { "lt", "LessThan" },
            { "gt", "GreaterThan" },
            { "lbrace", "LeftBrace" },
            { "rbrace", "RightBrace" },
            { "lbracket", "LeftBracket" },
            { "rbracket", "RightBracket" },
            { "paste", "Paste" },
            { "copy", "Copy" },
            { "snippets", "Snippets" },
            { "hidekb", "HideKeyboard" }
        };

        private static string CustomName
        {
            get { return Localized.Get("KeyBarEditor_CustomKey", "自定义按键"); }
        }

        public static string Describe(KeyBarKey key)
        {
            if (key == null || string.IsNullOrEmpty(key.Id))
            {
                return CustomName;
            }
            string suffix;
            if (ResourceSuffixes.TryGetValue(key.Id, out suffix))
            {
                // 第二参兜底仅防资源表缺键（O16 起文案事实来源在 resw）。
                return Localized.Get("KeyBar_Key_" + suffix, FallbackOf(suffix));
            }
            // F1–F12 等功能键不逐条列名，按类别给一句能看懂的说明。
            if (key.Kind == KeyBarKeyKind.Function)
            {
                return Localized.Format("KeyBarEditor_FunctionKey", "功能键 {0}",
                    string.IsNullOrEmpty(key.Label) ? key.Id : key.Label);
            }
            if (key.Kind == KeyBarKeyKind.Character)
            {
                return Localized.Get("KeyBarEditor_CharacterKey", "字符键");
            }
            if (key.Kind == KeyBarKeyKind.Modifier)
            {
                return Localized.Get("KeyBarEditor_ModifierKey", "修饰键");
            }
            if (key.Kind == KeyBarKeyKind.Action)
            {
                return Localized.Get("KeyBarEditor_QuickAction", "快捷操作");
            }
            return CustomName;
        }

        // 资源表缺键时的中文兜底（与 KeyBar_Key_* 的 zh-CN 值一致）。
        // gate:resw-fallback-start —— 兜底表本身允许中文字面量（resw 为事实来源）
        private static string FallbackOf(string suffix)
        {
            switch (suffix)
            {
                case "Esc": return "Esc 键";
                case "Tab": return "Tab 键";
                case "Ctrl": return "Ctrl 键";
                case "Alt": return "Alt 键";
                case "Shift": return "Shift 键";
                case "Up": return "向上方向键";
                case "Down": return "向下方向键";
                case "Left": return "向左方向键";
                case "Right": return "向右方向键";
                case "Home": return "Home 键";
                case "End": return "End 键";
                case "PgUp": return "上一页";
                case "PgDn": return "下一页";
                case "Ins": return "插入键";
                case "Del": return "删除键";
                case "Pipe": return "管道符";
                case "Slash": return "正斜杠";
                case "Backslash": return "反斜杠";
                case "Minus": return "减号";
                case "Underscore": return "下划线";
                case "Tilde": return "波浪号";
                case "Colon": return "冒号";
                case "Semicolon": return "分号";
                case "SingleQuote": return "单引号";
                case "DoubleQuote": return "双引号";
                case "Backtick": return "反引号";
                case "LessThan": return "小于号";
                case "GreaterThan": return "大于号";
                case "LeftBrace": return "左大括号";
                case "RightBrace": return "右大括号";
                case "LeftBracket": return "左方括号";
                case "RightBracket": return "右方括号";
                case "Paste": return "粘贴";
                case "Copy": return "复制";
                case "Snippets": return "代码片段";
                case "HideKeyboard": return "隐藏键盘";
                default: return string.Empty;
            }
        }
        // gate:resw-fallback-end
    }

    public sealed partial class KeyBarLayoutEditorPage : Page
    {
        // W06：页头返回按钮（与硬件返回键同一条处理链）。
        private void OnHeaderBackRequested(object sender, System.EventArgs e)
        {
            NavigationService nav;
            if (ServiceRegistry.TryGet(out nav))
            {
                nav.RequestBack();
            }
        }

        private readonly SettingsRepository _settings;

        public KeyBarLayoutEditorPage()
        {
            _settings = AppServices.Current.Settings;
            Available = new ObservableCollection<KeyBarKeyItem>();
            Selected = new ObservableCollection<KeyBarKeyItem>();
            this.InitializeComponent();
            AvailableList.ItemsSource = Available;
            SelectedList.ItemsSource = Selected;
            Reload();
        }

        public ObservableCollection<KeyBarKeyItem> Available { get; private set; }

        public ObservableCollection<KeyBarKeyItem> Selected { get; private set; }

        // V01a 评审回补：hidekb 键面是 IconKeyboard 字形，Core 目录 Label 留空；
        // 编辑器行首改显示语义化文本（resw 双语），不再渲染 ⌨。
        private static string DisplayLabel(KeyBarKey key)
        {
            if (key.Action == KeyBarAction.HideKeyboard)
            {
                return ResourceLoader.GetForCurrentView().GetString("KeyBar_HideKeyboardLabel");
            }
            return key.Label;
        }

        private void Reload()
        {
            Available.Clear();
            Selected.Clear();
            string stored;
            try
            {
                stored = _settings.KeyBarLayout;
            }
            catch (Exception)
            {
                stored = KeyBarLayout.DefaultString;
            }
            IReadOnlyList<KeyBarKey> selectedKeys = KeyBarLayout.Parse(stored);
            var selectedIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < selectedKeys.Count; i++)
            {
                selectedIds.Add(selectedKeys[i].Id);
                Selected.Add(new KeyBarKeyItem
                {
                    Id = selectedKeys[i].Id,
                    Label = DisplayLabel(selectedKeys[i]),
                    Description = KeyBarKeyNames.Describe(selectedKeys[i])
                });
            }
            IReadOnlyList<string> known = KeyBarLayout.KnownIds;
            for (int i = 0; i < known.Count; i++)
            {
                if (selectedIds.Contains(known[i]))
                {
                    continue;
                }
                KeyBarKey key;
                if (KeyBarLayout.TryGet(known[i], out key))
                {
                    Available.Add(new KeyBarKeyItem
                    {
                        Id = key.Id,
                        Label = DisplayLabel(key),
                        Description = KeyBarKeyNames.Describe(key)
                    });
                }
            }
        }

        private void Save()
        {
            var ids = new List<string>(Selected.Count);
            for (int i = 0; i < Selected.Count; i++)
            {
                ids.Add(Selected[i].Id);
            }
            try
            {
                _settings.KeyBarLayout = KeyBarLayout.Serialize(ids);
            }
            catch (Exception)
            {
            }
        }

        private void OnAddClick(object sender, RoutedEventArgs e)
        {
            var item = AvailableList.SelectedItem as KeyBarKeyItem;
            if (item == null)
            {
                return;
            }
            Available.Remove(item);
            Selected.Add(item);
            Save();
        }

        private void OnRemoveClick(object sender, RoutedEventArgs e)
        {
            var item = SelectedList.SelectedItem as KeyBarKeyItem;
            if (item == null)
            {
                return;
            }
            Selected.Remove(item);
            InsertAvailableSorted(item);
            Save();
        }

        private void OnMoveUpClick(object sender, RoutedEventArgs e)
        {
            var item = SelectedList.SelectedItem as KeyBarKeyItem;
            if (item == null)
            {
                return;
            }
            int index = Selected.IndexOf(item);
            if (index <= 0)
            {
                return;
            }
            Selected.Move(index, index - 1);
            Save();
        }

        private void OnMoveDownClick(object sender, RoutedEventArgs e)
        {
            var item = SelectedList.SelectedItem as KeyBarKeyItem;
            if (item == null)
            {
                return;
            }
            int index = Selected.IndexOf(item);
            if (index < 0 || index >= Selected.Count - 1)
            {
                return;
            }
            Selected.Move(index, index + 1);
            Save();
        }

        private void OnResetClick(object sender, RoutedEventArgs e)
        {
            try
            {
                _settings.KeyBarLayout = KeyBarLayout.DefaultString;
            }
            catch (Exception)
            {
            }
            Reload();
            Toast.Show("已恢复默认布局");
        }

        private void InsertAvailableSorted(KeyBarKeyItem item)
        {
            for (int i = 0; i < Available.Count; i++)
            {
                if (string.Compare(item.Id, Available[i].Id, StringComparison.Ordinal) < 0)
                {
                    Available.Insert(i, item);
                    return;
                }
            }
            Available.Add(item);
        }
    }
}
