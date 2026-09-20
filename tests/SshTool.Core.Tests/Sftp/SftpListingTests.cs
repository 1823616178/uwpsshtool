using System;
using System.Collections.Generic;
using System.Linq;
using SshTool.Core.Sftp;
using Xunit;

namespace SshTool.Core.Tests.Sftp
{
    // F03：目录列表纯逻辑（排序 / 隐藏文件 / 面包屑 / 大小与时间显示）。
    public class SftpListingTests
    {
        private static RemoteEntry Dir(string name)
        {
            return new RemoteEntry(name, "/d/" + name, RemoteEntryType.Directory,
                -1, 0x1FF, DateTime.MinValue, string.Empty);
        }

        private static RemoteEntry File(string name, long size, DateTime mtimeUtc)
        {
            return new RemoteEntry(name, "/d/" + name, RemoteEntryType.File,
                size, 0x1A4, mtimeUtc, string.Empty);
        }

        private static RemoteEntry Link(string name)
        {
            return new RemoteEntry(name, "/d/" + name, RemoteEntryType.Symlink,
                -1, 0xA1FF, DateTime.MinValue, "/d/other");
        }

        [Fact]
        public void Sort_Name_FoldersFirstThenIgnoreCase()
        {
            var entries = new List<RemoteEntry>
            {
                File("beta", 1, DateTime.MinValue),
                Dir("Zeta"),
                File("alpha", 2, DateTime.MinValue)
            };
            IReadOnlyList<RemoteEntry> sorted = SftpListing.Sort(entries, SftpSortMode.Name, true);
            Assert.Equal(new[] { "Zeta", "alpha", "beta" }, sorted.Select(e => e.Name).ToArray());
        }

        [Fact]
        public void Sort_Size_DescUnknownLast()
        {
            var entries = new List<RemoteEntry>
            {
                File("small", 10, DateTime.MinValue),
                File("big", 100, DateTime.MinValue),
                new RemoteEntry("unknown", "/d/unknown", RemoteEntryType.File, -1, 0x1A4,
                    DateTime.MinValue, string.Empty)
            };
            IReadOnlyList<RemoteEntry> sorted = SftpListing.Sort(entries, SftpSortMode.Size, true);
            Assert.Equal(new[] { "big", "small", "unknown" }, sorted.Select(e => e.Name).ToArray());
        }

        [Fact]
        public void Sort_Mtime_NewestFirstUnknownLast()
        {
            var entries = new List<RemoteEntry>
            {
                File("old", 1, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
                File("new", 1, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)),
                File("none", 1, DateTime.MinValue)
            };
            IReadOnlyList<RemoteEntry> sorted = SftpListing.Sort(entries, SftpSortMode.Mtime, true);
            Assert.Equal(new[] { "new", "old", "none" }, sorted.Select(e => e.Name).ToArray());
        }

        [Fact]
        public void Sort_HiddenFilteredUnlessRequested()
        {
            var entries = new List<RemoteEntry>
            {
                File("app.log", 1, DateTime.MinValue),
                File(".bashrc", 1, DateTime.MinValue),
                Dir(".config"),
                File("data", 1, DateTime.MinValue)
            };
            IReadOnlyList<RemoteEntry> shown = SftpListing.Sort(entries, SftpSortMode.Name, false);
            Assert.Equal(new[] { "app.log", "data" }, shown.Select(e => e.Name).ToArray());

            IReadOnlyList<RemoteEntry> all = SftpListing.Sort(entries, SftpSortMode.Name, true);
            Assert.Equal(4, all.Count);
        }

        [Fact]
        public void Sort_DoesNotMutateInput()
        {
            var entries = new List<RemoteEntry>
            {
                File("b", 1, DateTime.MinValue),
                File("a", 2, DateTime.MinValue)
            };
            SftpListing.Sort(entries, SftpSortMode.Name, true);
            Assert.Equal("b", entries[0].Name);
        }

        [Fact]
        public void Sort_NullEntries_Safe()
        {
            IReadOnlyList<RemoteEntry> sorted = SftpListing.Sort(null, SftpSortMode.Name, true);
            Assert.Empty(sorted);
        }

        [Fact]
        public void IsHidden_DotPrefix()
        {
            Assert.True(SftpListing.IsHidden(".git"));
            Assert.True(SftpListing.IsHidden("."));
            Assert.False(SftpListing.IsHidden("app.log"));
            Assert.False(SftpListing.IsHidden(""));
        }

        [Fact]
        public void Breadcrumb_SegmentsWithFullPath()
        {
            IReadOnlyList<SftpBreadcrumb> items = SftpListing.Breadcrumb("/home/root/logs");
            Assert.Equal(4, items.Count);
            Assert.Equal("/", items[0].Name);
            Assert.Equal("/", items[0].Path);
            Assert.Equal("home", items[1].Name);
            Assert.Equal("/home", items[1].Path);
            Assert.Equal("root", items[2].Name);
            Assert.Equal("/home/root", items[2].Path);
            Assert.Equal("logs", items[3].Name);
            Assert.Equal("/home/root/logs", items[3].Path);
        }

        [Fact]
        public void Breadcrumb_RootAndNormalization()
        {
            IReadOnlyList<SftpBreadcrumb> root = SftpListing.Breadcrumb("/");
            Assert.Equal(1, root.Count);
            Assert.Equal("/", root[0].Path);

            IReadOnlyList<SftpBreadcrumb> weird = SftpListing.Breadcrumb("a//b/../c");
            Assert.Equal(new[] { "/", "a", "c" }, weird.Select(b => b.Name).ToArray());
        }

        [Fact]
        public void FormatSize_BinaryUnits()
        {
            Assert.Equal(string.Empty, SftpListing.FormatSize(-1));
            Assert.Equal("0 B", SftpListing.FormatSize(0));
            Assert.Equal("512 B", SftpListing.FormatSize(512));
            Assert.Equal("2 KB", SftpListing.FormatSize(2048));
            Assert.Equal("12.3 MB", SftpListing.FormatSize(12897089));
            Assert.Equal("2 GB", SftpListing.FormatSize((long)2 * 1024 * 1024 * 1024));
        }

        [Fact]
        public void FormatMtime_AbsoluteOrEmpty()
        {
            Assert.Equal(string.Empty, SftpListing.FormatMtime(DateTime.MinValue));
            string text = SftpListing.FormatMtime(
                new DateTime(2026, 9, 15, 2, 5, 0, DateTimeKind.Utc));
            // 格式为「MM-dd HH:mm」（本地时区）：日期开头、时间结尾。
            Assert.StartsWith("09-15", text);
            Assert.Contains(":", text);
        }

        [Fact]
        public void MinutesAgo_WindowAndClamps()
        {
            DateTime now = new DateTime(2026, 9, 20, 12, 30, 0, DateTimeKind.Local);
            Assert.Equal(-1, SftpListing.MinutesAgo(DateTime.MinValue, now));
            Assert.Equal(5, SftpListing.MinutesAgo(now.AddMinutes(-5), now));
            Assert.Equal(0, SftpListing.MinutesAgo(now.AddSeconds(-30), now));
            Assert.Equal(-1, SftpListing.MinutesAgo(now.AddMinutes(-61), now));
        }
    }
}
