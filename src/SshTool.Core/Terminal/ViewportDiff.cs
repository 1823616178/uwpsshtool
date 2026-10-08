using System;

namespace SshTool.Core.Terminal
{
    // opt/full-pass：回滚浏览（offset > 0）时的逐行差分。
    // 旧实现在 offset > 0 时每次 Pull 都整窗 CopyViewport + 全量重绘；而回看历史时
    // 后台输出仍在持续（ScrollController 锚定视口，内容其实不动），等于每帧白画一整屏。
    // 现在把新视口拷进暂存区，与上一帧逐行比较，只把变化的行拷回并在脏位图里置位。
    public static class ViewportDiff
    {
        // 返回变化的行数。cells / fresh 为 rows*cols*BytesPerCell；dirty 为 (rows+7)/8 字节，
        // bit (row % 8) of byte (row / 8)，与原生 CopyDirtyRows 相同。dirty 先清零再写。
        public static int Apply(byte[] cells, byte[] fresh, byte[] dirty, int rows, int cols)
        {
            if (cells == null || fresh == null || dirty == null || rows <= 0 || cols <= 0)
            {
                return 0;
            }
            int rowBytes = cols * TerminalCell.BytesPerCell;
            if (cells.Length < rows * rowBytes || fresh.Length < rows * rowBytes || dirty.Length < (rows + 7) / 8)
            {
                throw new ArgumentException("buffer too small");
            }
            Array.Clear(dirty, 0, dirty.Length);
            int changed = 0;
            for (int r = 0; r < rows; r++)
            {
                int start = r * rowBytes;
                if (RowEquals(cells, fresh, start, rowBytes))
                {
                    continue;
                }
                Buffer.BlockCopy(fresh, start, cells, start, rowBytes);
                dirty[r / 8] |= (byte)(1 << (r % 8));
                changed++;
            }
            return changed;
        }

        private static bool RowEquals(byte[] a, byte[] b, int start, int length)
        {
            int end = start + length;
            for (int i = start; i < end; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }
            return true;
        }
    }
}
