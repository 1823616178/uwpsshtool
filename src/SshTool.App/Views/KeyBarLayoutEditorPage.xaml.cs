using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SshTool.App.Infrastructure;
using SshTool.Core.Storage;
using SshTool.Core.Terminal;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace SshTool.App.Views
{
    public sealed class KeyBarKeyItem
    {
        public string Id { get; set; }
        public string Label { get; set; }
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
                    Label = selectedKeys[i].Label
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
                    Available.Add(new KeyBarKeyItem { Id = key.Id, Label = key.Label });
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
