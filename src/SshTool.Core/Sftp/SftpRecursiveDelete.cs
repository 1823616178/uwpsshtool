using System;
using System.Threading;
using System.Threading.Tasks;
using SshTool.Core.Common;

namespace SshTool.Core.Sftp
{
    // F03：远端删除（02-UI-DESIGN.md §5.17 行菜单「删除」）。
    // 文件/符号链接 → unlink；目录 → 递归（UI 先弹递归确认，本层只负责执行）。
    //
    // 纪律：深度优先、先删子项后删目录（后序，否则 rmdir 非空目录报 604）；
    // 符号链接不深入（目标可能成环或在树外），一律按链接本身 unlink；
    // 拒绝删根（"/"）；块间裁决 ct；任一步失败立即带原错误码返回
    // （已删部分不回滚——远端语义如此，UI 提示「部分内容可能已删除」）。
    public static class SftpRecursiveDelete
    {
        public static async Task<SftpResult> DeleteAsync(
            ISftpClient client, string path, CancellationToken cancellationToken)
        {
            if (client == null)
            {
                return SftpResult.Fail(SshErrorCode.InternalError, "null client");
            }
            string normalized = RemotePath.Normalize(path);
            if (normalized == RemotePath.Root)
            {
                return SftpResult.Fail(SshErrorCode.InternalError, "refuse to delete root");
            }
            try
            {
                // 类型判定：stat（不跟随链接）——目录才递归，其余（文件/链接/未知）按文件删。
                SftpEntryResult stat = await client.StatAsync(normalized, false)
                    .ConfigureAwait(false);
                if (!stat.Ok || stat.Entry == null)
                {
                    return ToResult(stat);
                }
                if (stat.Entry.IsDirectory && !stat.Entry.IsSymlink)
                {
                    return await DeleteTreeAsync(client, normalized, cancellationToken)
                        .ConfigureAwait(false);
                }
                SftpResult removed = await client.DeleteFileAsync(normalized)
                    .ConfigureAwait(false);
                return ToResult(removed);
            }
            catch (OperationCanceledException)
            {
                return SftpResult.Fail(SshErrorCode.SftpCancelled, string.Empty);
            }
        }

        private static async Task<SftpResult> DeleteTreeAsync(
            ISftpClient client, string dir, CancellationToken cancellationToken)
        {
            SftpDirResult listing = await client.ListDirectoryAsync(dir).ConfigureAwait(false);
            if (!listing.Ok)
            {
                return ToResult(listing);
            }
            if (listing.Entries != null)
            {
                for (int i = 0; i < listing.Entries.Count; i++)
                {
                    RemoteEntry entry = listing.Entries[i];
                    if (entry == null || string.IsNullOrEmpty(entry.Name))
                    {
                        continue;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.IsDirectory && !entry.IsSymlink)
                    {
                        SftpResult child = await DeleteTreeAsync(client, entry.Path, cancellationToken)
                            .ConfigureAwait(false);
                        if (!child.Ok)
                        {
                            return child;
                        }
                    }
                    else
                    {
                        SftpResult removed = await client.DeleteFileAsync(entry.Path)
                            .ConfigureAwait(false);
                        if (!removed.Ok)
                        {
                            return removed;
                        }
                    }
                }
            }
            SftpResult dirRemoved = await client.RemoveDirectoryAsync(dir).ConfigureAwait(false);
            return ToResult(dirRemoved);
        }

        private static SftpResult ToResult(SftpResult result)
        {
            if (result != null && result.Ok)
            {
                return SftpResult.Success();
            }
            SshErrorCode code = result != null ? result.Code : SshErrorCode.Unknown;
            string message = result != null ? result.Message : string.Empty;
            return SftpResult.Fail(code, message);
        }
    }
}
