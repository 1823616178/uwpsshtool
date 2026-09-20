using System;

namespace SshTool.Core.Sftp
{
    public enum RemoteEntryType
    {
        Unknown = 0,
        File = 1,
        Directory = 2,
        Symlink = 3,
        Other = 4
    }

    // F02：远端目录项（native SftpEntry 的 Core 镜像，只用 Core 自有类型）。
    // 未知字段用哨兵：Size/Permissions 为 -1（native HasSize/HasPermissions
    // 为假时），MtimeUtc 为 DateTime.MinValue，LinkTarget 非链接时为空串。
    public sealed class RemoteEntry
    {
        public RemoteEntry(string name, string path, RemoteEntryType type,
                           long size, int permissions, DateTime mtimeUtc, string linkTarget)
        {
            Name = name ?? string.Empty;
            Path = path ?? string.Empty;
            Type = type;
            Size = size;
            Permissions = permissions;
            MtimeUtc = mtimeUtc;
            LinkTarget = linkTarget ?? string.Empty;
        }

        public string Name { get; }
        public string Path { get; }
        public RemoteEntryType Type { get; }
        public long Size { get; }
        public int Permissions { get; }
        public DateTime MtimeUtc { get; }
        public string LinkTarget { get; }

        public bool IsDirectory
        {
            get { return Type == RemoteEntryType.Directory; }
        }

        public bool IsFile
        {
            get { return Type == RemoteEntryType.File; }
        }

        public bool IsSymlink
        {
            get { return Type == RemoteEntryType.Symlink; }
        }

        public bool HasSize
        {
            get { return Size >= 0; }
        }

        public bool HasPermissions
        {
            get { return Permissions >= 0; }
        }

        public bool HasMtime
        {
            get { return MtimeUtc != DateTime.MinValue; }
        }

        public RemoteEntry Clone()
        {
            return new RemoteEntry(Name, Path, Type, Size, Permissions, MtimeUtc, LinkTarget);
        }
    }
}
