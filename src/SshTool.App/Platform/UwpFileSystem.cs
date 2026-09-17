using System;
using System.IO;
using System.Threading.Tasks;
using SshTool.Core.Storage;
using Windows.Storage;

namespace SshTool.App.Platform
{
    // D02：IFileSystem 的 UWP 实现，根目录为 ApplicationData.LocalFolder；
    // 路径为 '/' 分隔相对路径（如 "data/hosts.json"），写入时自动建目录。
    public sealed class UwpFileSystem : IFileSystem
    {
        private readonly StorageFolder _root;

        public UwpFileSystem()
            : this(ApplicationData.Current.LocalFolder)
        {
        }

        internal UwpFileSystem(StorageFolder root)
        {
            _root = root;
        }

        public async Task<bool> ExistsAsync(string path)
        {
            return await TryGetFileAsync(path).ConfigureAwait(false) != null;
        }

        public async Task<string> ReadAllTextAsync(string path)
        {
            var file = await TryGetFileAsync(path).ConfigureAwait(false);
            if (file == null)
            {
                throw new FileNotFoundException("文件不存在: " + path);
            }
            return await FileIO.ReadTextAsync(file);
        }

        public async Task WriteAllTextAsync(string path, string contents)
        {
            string name;
            var folder = await EnsureParentAsync(path).ConfigureAwait(false);
            name = FileNameOf(path);
            var file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(file, contents);
        }

        public async Task MoveAndReplaceAsync(string sourcePath, string targetPath)
        {
            var source = await TryGetFileAsync(sourcePath).ConfigureAwait(false);
            if (source == null)
            {
                throw new FileNotFoundException("文件不存在: " + sourcePath);
            }
            var folder = await EnsureParentAsync(targetPath).ConfigureAwait(false);
            string name = FileNameOf(targetPath);
            var existing = await folder.TryGetItemAsync(name);
            if (existing is StorageFile target)
            {
                await source.MoveAndReplaceAsync(target);
            }
            else
            {
                await source.MoveAsync(folder, name, NameCollisionOption.FailIfExists);
            }
        }

        private async Task<StorageFile> TryGetFileAsync(string path)
        {
            var parts = Split(path);
            IStorageItem item = await _root.TryGetItemAsync(parts[0]);
            for (int i = 0; i < parts.Length && item != null; i++)
            {
                if (i > 0)
                {
                    var folder = item as StorageFolder;
                    if (folder == null)
                    {
                        return null;
                    }
                    item = await folder.TryGetItemAsync(parts[i]);
                }
            }
            return item as StorageFile;
        }

        private async Task<StorageFolder> EnsureParentAsync(string path)
        {
            var parts = Split(path);
            var folder = _root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                folder = await folder.CreateFolderAsync(parts[i], CreationCollisionOption.OpenIfExists);
            }
            return folder;
        }

        private static string FileNameOf(string path)
        {
            var parts = Split(path);
            return parts[parts.Length - 1];
        }

        private static string[] Split(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("路径为空");
            }
            return path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}
