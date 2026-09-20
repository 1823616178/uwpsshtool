using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SshTool.Core.Common;
using SshTool.Core.Sftp;
using SshTool.Core.Tests.Fakes;
using Xunit;

namespace SshTool.Core.Tests.Sftp
{
    // F03：递归删除（FakeSftpClient 挂 ISftpClient 级验证：后序删除、失败即停、
    // 链接不深入、根拒删、取消映射 606、空目录）。
    public class SftpRecursiveDeleteTests
    {
        private static FakeSftpClient Tree()
        {
            var client = new FakeSftpClient();
            client.Directories.Add("/d");
            client.Directories.Add("/d/sub");
            client.Directories.Add("/d/empty");
            client.Files["/d/a.log"] = Encoding.UTF8.GetBytes("a");
            client.Files["/d/sub/b.log"] = Encoding.UTF8.GetBytes("b");
            return client;
        }

        [Fact]
        public async Task Tree_DeletesFilesThenSubdirsThenDir()
        {
            FakeSftpClient client = Tree();
            SftpResult result = await SftpRecursiveDelete.DeleteAsync(
                client, "/d", CancellationToken.None);
            Assert.True(result.Ok);
            Assert.Empty(client.Files);
            Assert.Empty(client.Directories.Where(d => d.StartsWith("/d")));
            // 后序：最深子目录先收尾，/d 最后；子文件先于子目录。
            int subIndex = client.Calls.IndexOf("RmDir:/d/sub");
            int rootIndex = client.Calls.IndexOf("RmDir:/d");
            int fileIndex = client.Calls.IndexOf("Delete:/d/sub/b.log");
            Assert.True(fileIndex >= 0 && fileIndex < subIndex);
            Assert.True(subIndex < rootIndex);
        }

        [Fact]
        public async Task File_UnlinkOnly()
        {
            FakeSftpClient client = Tree();
            SftpResult result = await SftpRecursiveDelete.DeleteAsync(
                client, "/d/a.log", CancellationToken.None);
            Assert.True(result.Ok);
            Assert.False(client.Files.ContainsKey("/d/a.log"));
            Assert.True(client.Directories.Contains("/d"));
            // 只 unlink：一次 Delete，绝无 RmDir。
            Assert.Equal(1, client.Calls.Count(c => c.StartsWith("Delete")));
            Assert.Equal(0, client.Calls.Count(c => c.StartsWith("RmDir")));
        }

        [Fact]
        public async Task MissingPath_FailsWith602()
        {
            FakeSftpClient client = Tree();
            SftpResult result = await SftpRecursiveDelete.DeleteAsync(
                client, "/d/none", CancellationToken.None);
            Assert.False(result.Ok);
            Assert.Equal(SshErrorCode.SftpNoSuchFile, result.Code);
        }

        [Fact]
        public async Task EmptyDir_RmDirOnly()
        {
            FakeSftpClient client = Tree();
            SftpResult result = await SftpRecursiveDelete.DeleteAsync(
                client, "/d/empty", CancellationToken.None);
            Assert.True(result.Ok);
            Assert.False(client.Directories.Contains("/d/empty"));
            Assert.Equal(1, client.Calls.Count(c => c == "RmDir:/d/empty"));
        }

        [Fact]
        public async Task Symlink_DeletedAsLink_TargetKept()
        {
            var client = new FakeSftpClient();
            client.Directories.Add("/d");
            client.Directories.Add("/target");
            client.Files["/target/inner.log"] = new byte[3];
            client.Symlinks["/d/link"] = "/target";
            // 删 /d：link 是符号链接 → 只 unlink 链接本身，不递归进 /target。
            SftpResult result = await SftpRecursiveDelete.DeleteAsync(
                client, "/d", CancellationToken.None);
            Assert.True(result.Ok);
            Assert.False(client.Directories.Contains("/d"));
            Assert.False(client.Symlinks.ContainsKey("/d/link"));
            // 目标目录与其中文件原样保留。
            Assert.True(client.Directories.Contains("/target"));
            Assert.True(client.Files.ContainsKey("/target/inner.log"));
            Assert.DoesNotContain(client.Calls, c => c == "RmDir:/target");
        }

        [Fact]
        public async Task Root_Refused()
        {
            FakeSftpClient client = Tree();
            SftpResult result = await SftpRecursiveDelete.DeleteAsync(
                client, "/", CancellationToken.None);
            Assert.False(result.Ok);
            Assert.Equal(SshErrorCode.InternalError, result.Code);
            Assert.DoesNotContain(client.Calls, c => c.StartsWith("RmDir"));
        }

        [Fact]
        public async Task ErrorStopsTree_RemainderKept()
        {
            FakeSftpClient client = Tree();
            // 第一个文件删除失败 → 整树中止；已删的部分保持原样（无回滚）。
            client.FailPaths.Add("/d/a.log");
            SftpResult result = await SftpRecursiveDelete.DeleteAsync(
                client, "/d", CancellationToken.None);
            Assert.False(result.Ok);
            Assert.Equal(SshErrorCode.SftpTransferFailed, result.Code);
            Assert.DoesNotContain(client.Calls, c => c == "RmDir:/d");
        }

        [Fact]
        public async Task Cancelled_MapsTo606()
        {
            FakeSftpClient client = Tree();
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                SftpResult result = await SftpRecursiveDelete.DeleteAsync(
                    client, "/d", cts.Token);
                Assert.False(result.Ok);
                Assert.Equal(SshErrorCode.SftpCancelled, result.Code);
            }
        }

        [Fact]
        public async Task NullClient_InternalError()
        {
            SftpResult result = await SftpRecursiveDelete.DeleteAsync(
                null, "/d", CancellationToken.None);
            Assert.False(result.Ok);
            Assert.Equal(SshErrorCode.InternalError, result.Code);
        }
    }
}
