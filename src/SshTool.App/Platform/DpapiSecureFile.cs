using System;
using System.Threading.Tasks;
using SshTool.Core.Storage;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;
using Windows.Storage;
using Windows.Storage.Streams;

namespace SshTool.App.Platform
{
    // D03 / D10：DataProtectionProvider("LOCAL=user") 加密整段明文，落 LocalFolder/secure/secrets.bin。
    public sealed class DpapiSecureFile : ISecureFile
    {
        public const string DefaultRelativePath = "secure/secrets.bin";
        private const string Descriptor = "LOCAL=user";

        private readonly StorageFolder _root;
        private readonly string _relativePath;

        public DpapiSecureFile()
            : this(ApplicationData.Current.LocalFolder, DefaultRelativePath)
        {
        }

        internal DpapiSecureFile(StorageFolder root, string relativePath)
        {
            _root = root;
            _relativePath = string.IsNullOrEmpty(relativePath) ? DefaultRelativePath : relativePath;
        }

        public async Task<byte[]> ReadAsync()
        {
            StorageFile file = await TryGetFileAsync().ConfigureAwait(false);
            if (file == null)
            {
                return new byte[0];
            }
            IBuffer cipher = await FileIO.ReadBufferAsync(file);
            if (cipher == null || cipher.Length == 0)
            {
                return new byte[0];
            }
            IBuffer plain = await new DataProtectionProvider().UnprotectAsync(cipher);
            byte[] bytes;
            CryptographicBuffer.CopyToByteArray(plain, out bytes);
            return bytes ?? new byte[0];
        }

        public async Task WriteAsync(byte[] plaintext)
        {
            byte[] data = plaintext ?? new byte[0];
            IBuffer plain = CryptographicBuffer.CreateFromByteArray(data);
            IBuffer cipher = await new DataProtectionProvider(Descriptor).ProtectAsync(plain);
            StorageFolder folder = await EnsureParentAsync().ConfigureAwait(false);
            string name = FileNameOf(_relativePath);
            string tmpName = name + ".tmp";
            StorageFile tmp = await folder.CreateFileAsync(tmpName, CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteBufferAsync(tmp, cipher);
            IStorageItem existing = await folder.TryGetItemAsync(name);
            var target = existing as StorageFile;
            if (target != null)
            {
                await tmp.MoveAndReplaceAsync(target);
            }
            else
            {
                await tmp.MoveAsync(folder, name, NameCollisionOption.ReplaceExisting);
            }
        }

        private async Task<StorageFile> TryGetFileAsync()
        {
            string[] parts = _relativePath.Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            IStorageItem item = _root;
            for (int i = 0; i < parts.Length; i++)
            {
                var folder = item as StorageFolder;
                if (folder == null)
                {
                    return null;
                }
                item = await folder.TryGetItemAsync(parts[i]);
                if (item == null)
                {
                    return null;
                }
            }
            return item as StorageFile;
        }

        private async Task<StorageFolder> EnsureParentAsync()
        {
            string[] parts = _relativePath.Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            StorageFolder folder = _root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                folder = await folder.CreateFolderAsync(parts[i], CreationCollisionOption.OpenIfExists);
            }
            return folder;
        }

        private static string FileNameOf(string path)
        {
            string[] parts = path.Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            return parts[parts.Length - 1];
        }
    }
}
