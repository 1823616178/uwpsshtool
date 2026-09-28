using System;
using System.Threading.Tasks;
using SshTool.Core.Hosts;
using SshTool.Core.Models;
using Windows.UI.StartScreen;

namespace SshTool.App.Platform
{
    // W03（01-DESIGN §16.3）：把主机固定到开始屏幕。SecondaryTile 自 10240 起可用，W10M 15063 无需守卫。
    // 磁贴只带主机 id；显示名是固定时的快照，改名后需重新固定（系统不提供静默更新名称）。
    public static class HostTileService
    {
        private static readonly Uri Logo = new Uri("ms-appx:///Assets/Square150x150Logo.png");

        public static bool IsPinned(string hostId)
        {
            try
            {
                return SecondaryTile.Exists(HostLaunchLinks.TileId(hostId));
            }
            catch (Exception)
            {
                return false;
            }
        }

        // 返回用户是否确认固定（系统会弹确认，用户可拒绝）。
        public static async Task<bool> PinAsync(Host host)
        {
            if (host == null || string.IsNullOrEmpty(host.Id))
            {
                return false;
            }
            string name = string.IsNullOrWhiteSpace(host.Name) ? host.HostName : host.Name;
            var tile = new SecondaryTile(
                HostLaunchLinks.TileId(host.Id),
                name ?? string.Empty,
                HostLaunchLinks.TileArguments(host.Id),
                Logo,
                TileSize.Default);
            tile.VisualElements.ShowNameOnSquare150x150Logo = true;
            return await tile.RequestCreateAsync();
        }

        public static async Task<bool> UnpinAsync(string hostId)
        {
            string id = HostLaunchLinks.TileId(hostId);
            if (!SecondaryTile.Exists(id))
            {
                return true;
            }
            return await new SecondaryTile(id).RequestDeleteAsync();
        }
    }
}
