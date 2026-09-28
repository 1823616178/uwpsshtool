using System;
using System.Threading.Tasks;
using Windows.Storage;

namespace SshTool.App.Platform
{
    // W05（01-DESIGN §16.5）：「用其他应用打开」的下载缓存目录 TemporaryFolder\sftp-open。
    // 系统也会回收 TemporaryFolder，这里在启动时主动清掉超过 MaxAge 的文件，避免越积越多。
    public static class SftpOpenCache
    {
        private const string FolderName = "sftp-open";
        private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

        public static async Task<StorageFolder> GetFolderAsync()
        {
            return await ApplicationData.Current.TemporaryFolder
                .CreateFolderAsync(FolderName, CreationCollisionOption.OpenIfExists);
        }

        public static async Task CleanupAsync()
        {
            StorageFolder folder = await GetFolderAsync();
            DateTimeOffset cutoff = DateTimeOffset.Now - MaxAge;
            foreach (StorageFile file in await folder.GetFilesAsync())
            {
                if (file.DateCreated < cutoff)
                {
                    try
                    {
                        await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
                    }
                    catch (Exception)
                    {
                        // 仍被外部应用占用：下次再清。
                    }
                }
            }
        }
    }
}
