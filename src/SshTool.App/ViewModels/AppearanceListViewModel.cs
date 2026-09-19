using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using SshTool.App.Infrastructure;
using SshTool.App.Views;
using SshTool.Core.Appearance;
using SshTool.Core.Common;
using SshTool.Core.Models;
using SshTool.Core.Mvvm;
using Windows.UI.Xaml.Media;

namespace SshTool.App.ViewModels
{
    // A03：外观列表行（主题卡片：名称 + 8 色条 + 默认/内置徽标）。
    public sealed class AppearanceRow
    {
        public AppearanceRow(AppearanceProfile profile, bool isDefault)
        {
            Id = profile.Id;
            Name = profile.Name;
            IsBuiltIn = profile.BuiltIn;
            IsDefault = isDefault;
            Badge = isDefault ? "默认" : (profile.BuiltIn ? "内置" : string.Empty);
            Strip = BuildStrip(profile.Palette);
        }

        public string Id { get; private set; }
        public string Name { get; private set; }
        public bool IsBuiltIn { get; private set; }
        public bool IsDefault { get; private set; }
        public string Badge { get; private set; }
        public IList<SolidColorBrush> Strip { get; private set; }

        private static IList<SolidColorBrush> BuildStrip(IList<string> palette)
        {
            var strip = new List<SolidColorBrush>(8);
            for (int i = 0; i < 8; i++)
            {
                string hex = palette != null && i < palette.Count ? palette[i] : "#000000";
                strip.Add(AppearanceBrushes.FromHex(hex));
            }
            return strip;
        }
    }

    // #RRGGBB → 笔刷（非法值回退透明；列表页 8 色条用）。
    internal static class AppearanceBrushes
    {
        public static SolidColorBrush FromHex(string hex)
        {
            byte r;
            byte g;
            byte b;
            if (!TryParse(hex, out r, out g, out b))
            {
                return new SolidColorBrush(Windows.UI.Colors.Transparent);
            }
            return new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
        }

        private static bool TryParse(string hex, out byte r, out byte g, out byte b)
        {
            r = 0;
            g = 0;
            b = 0;
            if (string.IsNullOrEmpty(hex) || hex.Length != 7 || hex[0] != '#')
            {
                return false;
            }
            try
            {
                r = byte.Parse(hex.Substring(1, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture);
                g = byte.Parse(hex.Substring(3, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture);
                b = byte.Parse(hex.Substring(5, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    // A03：外观列表（02-UI-DESIGN.md §5.12）。增删改走 AppearanceService；
    // 删除确认与设默认的副作用（跟随默认主机刷新）由服务事件广播。
    public sealed class AppearanceListViewModel : ViewModelBase
    {
        private readonly AppServices _services;

        public AppearanceListViewModel(AppServices services)
        {
            if (services == null)
            {
                throw new ArgumentNullException("services");
            }
            _services = services;
            Items = new ObservableCollection<AppearanceRow>();
            NewCommand = new RelayCommand(() => Navigation.Navigate<AppearanceEditPage>(AppearanceEditArgs.New()));
        }

        public ObservableCollection<AppearanceRow> Items { get; private set; }
        public ICommand NewCommand { get; private set; }

        public void OpenEdit(AppearanceRow row)
        {
            if (row != null)
            {
                Navigation.Navigate<AppearanceEditPage>(AppearanceEditArgs.Edit(row.Id));
            }
        }

        public async Task RefreshAsync()
        {
            IReadOnlyList<AppearanceProfile> all = await _services.AppearanceService.ListAsync().ConfigureAwait(true);
            string defaultId = _services.Settings.DefaultAppearanceId;
            Items.Clear();
            for (int i = 0; i < all.Count; i++)
            {
                Items.Add(new AppearanceRow(all[i], string.Equals(all[i].Id, defaultId, StringComparison.Ordinal)));
            }
        }

        public async Task<IReadOnlyList<Host>> GetReferencingHostsAsync(AppearanceRow row)
        {
            if (row == null)
            {
                return new Host[0];
            }
            return await _services.AppearanceService.GetReferencingHostsAsync(row.Id).ConfigureAwait(true);
        }

        // 删除：引用主机由服务回退为 null；删的是当前默认则默认回到内置。
        public async Task DeleteConfirmedAsync(AppearanceRow row)
        {
            if (row == null)
            {
                return;
            }
            try
            {
                bool wasDefault = string.Equals(_services.Settings.DefaultAppearanceId, row.Id, StringComparison.Ordinal);
                await _services.AppearanceService.DeleteAsync(row.Id).ConfigureAwait(true);
                if (wasDefault)
                {
                    await _services.AppearanceService.SetDefaultAsync(AppearanceResolver.DefaultBuiltInId).ConfigureAwait(true);
                }
                await RefreshAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                OnError(ex);
            }
        }

        public async Task SetDefaultAsync(AppearanceRow row)
        {
            if (row == null)
            {
                return;
            }
            try
            {
                await _services.AppearanceService.SetDefaultAsync(row.Id).ConfigureAwait(true);
                await RefreshAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                OnError(ex);
            }
        }

        // A04：导入落盘（解析器已分配 id、填好默认值；失败记日志并返回 false）。
        public async Task<bool> ImportProfilesAsync(IReadOnlyList<AppearanceProfile> profiles)
        {
            if (profiles == null || profiles.Count == 0)
            {
                return false;
            }
            try
            {
                for (int i = 0; i < profiles.Count; i++)
                {
                    await _services.AppearanceService.AddAsync(profiles[i]).ConfigureAwait(true);
                }
                return true;
            }
            catch (Exception ex)
            {
                OnError(ex);
                return false;
            }
        }

        // 复制为新主题（内置/用户均可）：新 id、BuiltIn=false，名后加" 副本"。
        public async Task DuplicateAsync(AppearanceRow row)
        {
            if (row == null)
            {
                return;
            }
            try
            {
                IReadOnlyList<AppearanceProfile> all = await _services.AppearanceService.ListAsync().ConfigureAwait(true);
                AppearanceProfile src = null;
                for (int i = 0; i < all.Count; i++)
                {
                    if (string.Equals(all[i].Id, row.Id, StringComparison.Ordinal))
                    {
                        src = all[i];
                        break;
                    }
                }
                if (src == null)
                {
                    return;
                }
                AppearanceProfile copy = src.Clone();
                copy.Id = IdGenerator.NewId();
                copy.BuiltIn = false;
                copy.Name = (src.Name ?? "主题") + " 副本";
                await _services.AppearanceService.AddAsync(copy).ConfigureAwait(true);
                await RefreshAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                OnError(ex);
            }
        }

        private void OnError(Exception ex)
        {
            if (ex != null)
            {
                Logger.Log(LogLevel.Error, "AppearanceList", ex.GetType().Name);
            }
        }
    }
}
