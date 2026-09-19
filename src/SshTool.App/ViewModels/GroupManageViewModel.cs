using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using SshTool.App.Dialogs;
using SshTool.App.Infrastructure;
using SshTool.Core.Common;
using SshTool.Core.Models;
using SshTool.Core.Mvvm;
using SshTool.Core.Storage;
using SshTool.Core.Validation;

namespace SshTool.App.ViewModels
{
    public sealed class GroupRow : ObservableObject
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Color { get; set; }
        public int Order { get; set; }
        public int HostCount { get; set; }
        public int TunnelCount { get; set; }
    }

    public sealed class GroupManageViewModel : ViewModelBase
    {
        private readonly AppServices _services;

        public GroupManageViewModel(AppServices services)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }
            _services = services;
            Groups = new ObservableCollection<GroupRow>();
            AddCommand = new AsyncCommand(AddAsync, onError: OnError);
            var ignore = RefreshAsync();
        }

        public ObservableCollection<GroupRow> Groups { get; private set; }
        public ICommand AddCommand { get; private set; }

        public async Task RefreshAsync()
        {
            IReadOnlyList<HostGroup> groups = await _services.Groups.GetAllAsync().ConfigureAwait(true);
            IReadOnlyList<Host> hosts = await _services.Hosts.GetAllAsync().ConfigureAwait(true);
            IReadOnlyList<Tunnel> tunnels = await _services.Tunnels.GetAllAsync().ConfigureAwait(true);
            var rows = new List<GroupRow>();
            for (int i = 0; i < groups.Count; i++)
            {
                HostGroup g = groups[i];
                int hc = 0;
                int tc = 0;
                for (int h = 0; h < hosts.Count; h++)
                {
                    if (hosts[h].GroupId == g.Id)
                    {
                        hc++;
                    }
                }
                for (int t = 0; t < tunnels.Count; t++)
                {
                    if (tunnels[t].GroupId == g.Id)
                    {
                        tc++;
                    }
                }
                rows.Add(new GroupRow
                {
                    Id = g.Id,
                    Name = g.Name,
                    Color = g.Color,
                    Order = g.Order,
                    HostCount = hc,
                    TunnelCount = tc
                });
            }
            rows.Sort((a, b) => a.Order.CompareTo(b.Order));
            Groups.Clear();
            for (int i = 0; i < rows.Count; i++)
            {
                Groups.Add(rows[i]);
            }
        }

        public async Task AddAsync()
        {
            HostGroup group = Defaults.NewGroup("新分组");
            group.Order = NextOrder();
            ValidationResult v = GroupValidator.Validate(group);
            if (!v.IsValid)
            {
                return;
            }
            await _services.Groups.AddAsync(group, ChangeOrigin.User).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
        }

        public async Task RenameAsync(GroupRow row, string name)
        {
            HostGroup group = await _services.Groups.GetByIdAsync(row.Id).ConfigureAwait(true);
            if (group == null)
            {
                return;
            }
            group.Name = name ?? string.Empty;
            ValidationResult v = GroupValidator.Validate(group);
            if (!v.IsValid)
            {
                return;
            }
            await _services.Groups.UpdateAsync(group, ChangeOrigin.User).ConfigureAwait(true);
            row.Name = group.Name;
        }

        public async Task SetColorAsync(GroupRow row, string color)
        {
            HostGroup group = await _services.Groups.GetByIdAsync(row.Id).ConfigureAwait(true);
            if (group == null)
            {
                return;
            }
            group.Color = color;
            ValidationResult v = GroupValidator.Validate(group);
            if (!v.IsValid)
            {
                return;
            }
            await _services.Groups.UpdateAsync(group, ChangeOrigin.User).ConfigureAwait(true);
            row.Color = group.Color;
        }

        public async Task MoveAsync(GroupRow row, int delta)
        {
            int index = IndexOf(row.Id);
            int target = index + delta;
            if (index < 0 || target < 0 || target >= Groups.Count)
            {
                return;
            }
            GroupRow other = Groups[target];
            HostGroup a = await _services.Groups.GetByIdAsync(row.Id).ConfigureAwait(true);
            HostGroup b = await _services.Groups.GetByIdAsync(other.Id).ConfigureAwait(true);
            if (a == null || b == null)
            {
                return;
            }
            int tmp = a.Order;
            a.Order = b.Order;
            b.Order = tmp;
            await _services.Groups.UpdateAsync(a, ChangeOrigin.User).ConfigureAwait(true);
            await _services.Groups.UpdateAsync(b, ChangeOrigin.User).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
        }

        public async Task DeleteAsync(GroupRow row)
        {
            string message = "删除分组「" + row.Name + "」？";
            if (row.HostCount > 0 || row.TunnelCount > 0)
            {
                message += " 将影响 " + row.HostCount.ToString() + " 台主机、"
                    + row.TunnelCount.ToString() + " 条隧道（分组会清空，主机与隧道保留）。";
            }
            ConfirmDialogResult confirm = await ConfirmDialog.ShowAsync(
                "删除分组", message, "删除", "取消", true).ConfigureAwait(true);
            if (!confirm.Confirmed)
            {
                return;
            }
            await _services.Config.DeleteGroupAsync(row.Id).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
        }

        private int NextOrder()
        {
            int max = 0;
            for (int i = 0; i < Groups.Count; i++)
            {
                if (Groups[i].Order > max)
                {
                    max = Groups[i].Order;
                }
            }
            return max + 1;
        }

        private int IndexOf(string id)
        {
            for (int i = 0; i < Groups.Count; i++)
            {
                if (Groups[i].Id == id)
                {
                    return i;
                }
            }
            return -1;
        }

        private void OnError(Exception ex)
        {
            if (ex != null)
            {
                Logger.Log(LogLevel.Error, "GroupManage", ex.GetType().Name);
            }
        }
    }
}
