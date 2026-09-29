using System;
using System.Collections.Generic;

namespace SshTool.Core.Sftp
{
    public enum SftpSortMode
    {
        Name = 0,
        Size = 1,
        Mtime = 2
    }

    // F03：面包屑段（02-UI-DESIGN.md §5.17 面包屑，可点回跳）。
    // Name 为根时是 "/"，其余是最后一段目录名；Path 为完整远端路径。
    public sealed class SftpBreadcrumb
    {
        public SftpBreadcrumb(string name, string path)
        {
            Name = name ?? string.Empty;
            Path = path ?? string.Empty;
        }

        public string Name { get; }
        public string Path { get; }
    }

    // F03：SFTP 目录列表的纯逻辑（02-UI-DESIGN.md §5.17 排序 + 隐藏文件开关 + 面包屑）。
    // 纯函数，可单测；SftpViewModel 与页面只做绑定。
    // 排序约定：目录恒在文件/链接之前；名称 OrdinalIgnoreCase 升序；
    // 大小降序（-1 未知殿后）；时间新的在前（MinValue 未知殿后）。
    public static class SftpListing
    {
        // 排序 + 过滤（不修改入参，返回新列表）。
        public static IReadOnlyList<RemoteEntry> Sort(
            IReadOnlyList<RemoteEntry> entries, SftpSortMode mode, bool showHidden)
        {
            List<RemoteEntry> list = Filter(entries, showHidden);
            Comparison<RemoteEntry> compare;
            switch (mode)
            {
                case SftpSortMode.Size:
                    compare = CompareBySize;
                    break;
                case SftpSortMode.Mtime:
                    compare = CompareByMtime;
                    break;
                default:
                    compare = CompareByName;
                    break;
            }
            list.Sort(compare);
            return list.AsReadOnly();
        }

        public static List<RemoteEntry> Filter(IReadOnlyList<RemoteEntry> entries, bool showHidden)
        {
            var list = new List<RemoteEntry>();
            if (entries == null)
            {
                return list;
            }
            for (int i = 0; i < entries.Count; i++)
            {
                RemoteEntry entry = entries[i];
                if (entry == null || (!showHidden && IsHidden(entry.Name)))
                {
                    continue;
                }
                list.Add(entry);
            }
            return list;
        }

        // ".git"、".bashrc" 等点开头的名称视为隐藏（与 ls 约定一致；"."/".." 已被 native 滤掉）。
        public static bool IsHidden(string name)
        {
            return !string.IsNullOrEmpty(name) && name[0] == '.';
        }

        // "/home/root/logs" → ["/"(根), "home", "root", "logs"]；根 → 单段 "/"。
        public static IReadOnlyList<SftpBreadcrumb> Breadcrumb(string path)
        {
            string normalized = RemotePath.Normalize(path);
            var items = new List<SftpBreadcrumb>();
            if (normalized == RemotePath.Root)
            {
                items.Add(new SftpBreadcrumb("/", RemotePath.Root));
                return items.AsReadOnly();
            }
            items.Add(new SftpBreadcrumb("/", RemotePath.Root));
            string[] parts = normalized.Split('/');
            string current = string.Empty;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0)
                {
                    continue;
                }
                current = current.Length == 0 ? "/" + parts[i] : current + "/" + parts[i];
                items.Add(new SftpBreadcrumb(parts[i], current));
            }
            return items.AsReadOnly();
        }

        // 大小显示：-1 未知 → 空串；<1KiB → "512 B"；否则一档一位小数（0.#，不变文化）。
        public static string FormatSize(long bytes)
        {
            if (bytes < 0)
            {
                return string.Empty;
            }
            if (bytes < 1024)
            {
                return bytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + " B";
            }
            const double Ki = 1024d;
            const double Mi = 1024d * 1024d;
            const double Gi = 1024d * 1024d * 1024d;
            if (bytes < Mi)
            {
                return (bytes / Ki).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " KB";
            }
            if (bytes < Gi)
            {
                return (bytes / Mi).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " MB";
            }
            return (bytes / Gi).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " GB";
        }

        // 时间显示：MM-dd HH:mm（本地时区）；未知（MinValue）→ 空串。
        // 1 小时内的相对时间文案由 UI 层出（§7 相对时间），判定用 MinutesAgo。
        public static string FormatMtime(DateTime mtimeUtc)
        {
            if (mtimeUtc == DateTime.MinValue)
            {
                return string.Empty;
            }
            return mtimeUtc.ToLocalTime()
                .ToString("MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        }

        // 距 nowLocal 的分钟数；不足 0 分钟返回 0；≥60 分钟返回 -1（调用方改用绝对时间）。
        public static int MinutesAgo(DateTime mtimeUtc, DateTime localNow)
        {
            if (mtimeUtc == DateTime.MinValue)
            {
                return -1;
            }
            double minutes = (localNow - mtimeUtc.ToLocalTime()).TotalMinutes;
            if (minutes < 0)
            {
                return 0;
            }
            if (minutes >= 60)
            {
                return -1;
            }
            return (int)minutes;
        }

        private static int CompareByName(RemoteEntry a, RemoteEntry b)
        {
            int d = DirFirst(a, b);
            if (d != 0)
            {
                return d;
            }
            int n = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            if (n != 0)
            {
                return n;
            }
            return string.CompareOrdinal(a.Path, b.Path);
        }

        private static int CompareBySize(RemoteEntry a, RemoteEntry b)
        {
            int d = DirFirst(a, b);
            if (d != 0)
            {
                return d;
            }
            // 未知（-1）殿后；其余大在前。
            if (a.Size < 0 && b.Size < 0)
            {
                return CompareByName(a, b);
            }
            if (a.Size < 0)
            {
                return 1;
            }
            if (b.Size < 0)
            {
                return -1;
            }
            if (a.Size != b.Size)
            {
                return a.Size > b.Size ? -1 : 1;
            }
            return CompareByName(a, b);
        }

        private static int CompareByMtime(RemoteEntry a, RemoteEntry b)
        {
            int d = DirFirst(a, b);
            if (d != 0)
            {
                return d;
            }
            if (a.MtimeUtc == DateTime.MinValue && b.MtimeUtc == DateTime.MinValue)
            {
                return CompareByName(a, b);
            }
            if (a.MtimeUtc == DateTime.MinValue)
            {
                return 1;
            }
            if (b.MtimeUtc == DateTime.MinValue)
            {
                return -1;
            }
            if (a.MtimeUtc != b.MtimeUtc)
            {
                return a.MtimeUtc > b.MtimeUtc ? -1 : 1;
            }
            return CompareByName(a, b);
        }

        private static int DirFirst(RemoteEntry a, RemoteEntry b)
        {
            return DirRank(a).CompareTo(DirRank(b));
        }

        private static int DirRank(RemoteEntry e)
        {
            return e != null && e.IsDirectory && !e.IsSymlink ? 0 : 1;
        }

        // G08：根据文件名后缀与条目属性分类图标资源键（Tokens.xaml 中的 Icon* 键）。
        public static string ResolveIconToken(string fileName, bool isDirectory, bool isSymlink)
        {
            if (isSymlink)
            {
                return "IconFileSymlink";
            }
            if (isDirectory)
            {
                return "IconFolder";
            }
            return ResolveFileIconToken(fileName);
        }

        public static string ResolveFileIconToken(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return "IconDocument";
            }
            string lower = fileName.ToLowerInvariant();
            if (lower.EndsWith(".tar.gz") || lower.EndsWith(".tar.bz2") || lower.EndsWith(".tar.xz") || lower.EndsWith(".tar.zst"))
            {
                return "IconFileArchive";
            }

            int dotIndex = lower.LastIndexOf('.');
            if (dotIndex < 0 || dotIndex == lower.Length - 1)
            {
                return "IconDocument";
            }

            string ext = lower.Substring(dotIndex + 1);
            switch (ext)
            {
                // 图片
                case "png":
                case "jpg":
                case "jpeg":
                case "gif":
                case "bmp":
                case "webp":
                case "svg":
                case "ico":
                case "tif":
                case "tiff":
                    return "IconFileImage";

                // 压缩包
                case "zip":
                case "tar":
                case "gz":
                case "tgz":
                case "bz2":
                case "xz":
                case "7z":
                case "rar":
                case "zst":
                case "iso":
                    return "IconFileArchive";

                // 代码与脚本
                case "sh":
                case "bash":
                case "zsh":
                case "py":
                case "js":
                case "ts":
                case "jsx":
                case "tsx":
                case "c":
                case "cpp":
                case "cc":
                case "cxx":
                case "h":
                case "hpp":
                case "cs":
                case "go":
                case "rs":
                case "java":
                case "kt":
                case "rb":
                case "php":
                case "pl":
                case "lua":
                case "sql":
                case "html":
                case "htm":
                case "css":
                case "scss":
                case "less":
                case "ps1":
                case "bat":
                case "cmd":
                case "asm":
                    return "IconFileCode";

                // 配置
                case "conf":
                case "cfg":
                case "config":
                case "ini":
                case "json":
                case "yaml":
                case "yml":
                case "toml":
                case "xml":
                case "env":
                case "properties":
                case "plist":
                    return "IconFileConfig";

                // 日志
                case "log":
                case "out":
                case "err":
                    return "IconFileLog";

                // 可执行与二进制
                case "exe":
                case "bin":
                case "dll":
                case "so":
                case "dylib":
                case "deb":
                case "rpm":
                case "apk":
                case "jar":
                case "appx":
                case "msi":
                case "o":
                case "a":
                    return "IconFileBinary";

                // 默认普通文本与文档
                default:
                    return "IconDocument";
            }
        }
    }
}
