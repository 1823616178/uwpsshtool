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

    // 内置键条键的中文显示名（KeyBarKey 只有 Id + 键面 Label，没有 Name/DisplayName）。
    // 未收录的 id 统一退化为「自定义按键」，绝不回落到 id。
    public static class KeyBarKeyNames
    {
        private static readonly Dictionary<string, string> ChineseNames = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "esc", "退出 Esc" },
            { "tab", "制表 Tab" },
            { "ctrl", "Ctrl 修饰键" },
            { "alt", "Alt 修饰键" },
            { "shift", "Shift 修饰键" },
            { "up", "方向键 上" },
            { "down", "方向键 下" },
            { "left", "方向键 左" },
            { "right", "方向键 右" },
            { "home", "行首 Home" },
            { "end", "行尾 End" },
            { "pgup", "向上翻页" },
            { "pgdn", "向下翻页" },
            { "ins", "插入模式" },
            { "del", "向后删除" },
            { "pipe", "字符 竖线" },
            { "slash", "字符 斜杠" },
            { "backslash", "字符 反斜杠" },
            { "minus", "字符 减号" },
            { "underscore", "字符 下划线" },
            { "tilde", "字符 波浪号" },
            { "colon", "字符 冒号" },
            { "semicolon", "字符 分号" },
            { "quote", "字符 单引号" },
            { "dquote", "字符 双引号" },
            { "backtick", "字符 反引号" },
            { "lt", "字符 小于号" },
            { "gt", "字符 大于号" },
            { "lbrace", "字符 左大括号" },
            { "rbrace", "字符 右大括号" },
            { "lbracket", "字符 左方括号" },
            { "rbracket", "字符 右方括号" },
            { "paste", "粘贴操作" },
            { "copy", "复制操作" },
            { "snippets", "片段面板" },
            { "hidekb", "隐藏键盘" }
        };

        private const string CustomName = "自定义按键";

        public static string Describe(KeyBarKey key)
        {
            if (key == null || string.IsNullOrEmpty(key.Id))
            {
                return CustomName;
            }
            string name;
            if (ChineseNames.TryGetValue(key.Id, out name))
            {
                return name;
            }
            // F1–F12 等功能键不逐条列名，按类别给一句能看懂的说明。
            if (key.Kind == KeyBarKeyKind.Function)
            {
                return "功能键 " + (string.IsNullOrEmpty(key.Label) ? key.Id : key.Label);
            }
            if (key.Kind == KeyBarKeyKind.Character)
            {
                return "字符键";
            }
            if (key.Kind == KeyBarKeyKind.Modifier)
            {
                return "修饰键";
            }
            if (key.Kind == KeyBarKeyKind.Action)
            {
                return "快捷操作";
            }
            return CustomName;
        }
    }

    public sealed partial class KeyBarLayoutEditorPage : Page
    {
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
