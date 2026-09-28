using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Storage;

namespace SshTool.App.Platform
{
    // W05（01-DESIGN §16.5）：分享目标的收件箱 LocalCacheFolder\share-inbox。
    // 分享窗口是独立视图，不能在里面启动应用服务（会话、调度器都绑主视图）；它只把文件复制进来，
    // 主视图下次进入主页时取出、让用户选主机并排队上传，上传完成前文件一直留在这里。
    public static class ShareInbox
    {
        private const string FolderName = "share-inbox";
        private const string OutboxName = "share-outbox";
        private static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

        private static async Task<StorageFolder> GetFolderAsync()
        {
            return await ApplicationData.Current.LocalCacheFolder
                .CreateFolderAsync(FolderName, CreationCollisionOption.OpenIfExists);
        }

        public static async Task<int> AddAsync(IReadOnlyList<IStorageItem> items)
        {
            StorageFolder folder = await GetFolderAsync();
            int copied = 0;
            if (items == null)
            {
                return 0;
            }
            foreach (IStorageItem item in items)
            {
                var file = item as StorageFile;
                if (file == null)
                {
                    continue; // 文件夹不支持（SFTP 递归上传不在本任务范围）
                }
                await file.CopyAsync(folder, file.Name, NameCollisionOption.GenerateUniqueName);
                copied++;
            }
            return copied;
        }

        public static async Task<int> CountAsync()
        {
            StorageFolder folder = await GetFolderAsync();
            return (await folder.GetFilesAsync()).Count;
        }

        // 取走全部待上传文件：移进 share-outbox\<批次>，收件箱随之清空，回到主页不会再次提示。
        // 批次目录留到上传结束后由启动清理（CleanupAsync）回收。
        public static async Task<IReadOnlyList<StorageFile>> TakeAsync()
        {
            StorageFolder inbox = await GetFolderAsync();
            IReadOnlyList<StorageFile> files = await inbox.GetFilesAsync();
            var taken = new List<StorageFile>();
            if (files.Count == 0)
            {
                return taken;
            }
            StorageFolder outbox = await ApplicationData.Current.LocalCacheFolder
                .CreateFolderAsync(OutboxName, CreationCollisionOption.OpenIfExists);
            StorageFolder batch = await outbox.CreateFolderAsync(
                DateTimeOffset.Now.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture),
                CreationCollisionOption.GenerateUniqueName);
            foreach (StorageFile file in files)
            {
                await file.MoveAsync(batch, file.Name, NameCollisionOption.GenerateUniqueName);
                taken.Add(file); // MoveAsync 后同一对象指向新位置
            }
            return taken;
        }

        public static async Task ClearAsync()
        {
            StorageFolder folder = await GetFolderAsync();
            foreach (StorageFile file in await folder.GetFilesAsync())
            {
                await TryDeleteAsync(file);
            }
        }

        // 启动时回收超过 MaxAge 的已取走批次。
        public static async Task CleanupAsync()
        {
            StorageFolder outbox = await ApplicationData.Current.LocalCacheFolder
                .CreateFolderAsync(OutboxName, CreationCollisionOption.OpenIfExists);
            DateTimeOffset cutoff = DateTimeOffset.Now - MaxAge;
            foreach (StorageFolder batch in await outbox.GetFoldersAsync())
            {
                if (batch.DateCreated < cutoff)
                {
                    try
                    {
                        await batch.DeleteAsync(StorageDeleteOption.PermanentDelete);
                    }
                    catch (Exception)
                    {
                        // 仍在上传：下次再清。
                    }
                }
            }
        }

        private static async Task TryDeleteAsync(StorageFile file)
        {
            try
            {
                await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
            catch (Exception)
            {
            }
        }
    }
}
