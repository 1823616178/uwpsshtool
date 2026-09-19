using System;
using System.Text;

namespace SshTool.Core.Terminal
{
    // 01-DESIGN.md §7.5：哨兵差分结果。Inserted 是去掉零宽空格后的新增文本；
    // Deleted 是哨兵被吃掉的字符数（0–2）。哨兵本身不是远端内容。
    public struct SentinelDiffResult : IEquatable<SentinelDiffResult>
    {
        public SentinelDiffResult(string inserted, int deleted)
        {
            Inserted = inserted ?? string.Empty;
            Deleted = deleted < 0 ? 0 : deleted;
        }

        public string Inserted { get; }

        public int Deleted { get; }

        public bool IsEmpty
        {
            get { return Inserted.Length == 0 && Deleted == 0; }
        }

        // 有新增（含整段替换）时只发新增；无新增才把删除交给调用方（组合态退格吃哨兵）。
        public bool ShouldSendDeletes
        {
            get { return Deleted > 0 && Inserted.Length == 0; }
        }

        public byte[] InsertedUtf8()
        {
            if (Inserted.Length == 0)
            {
                return new byte[0];
            }
            return Encoding.UTF8.GetBytes(Inserted);
        }

        public bool Equals(SentinelDiffResult other)
        {
            return Deleted == other.Deleted && Inserted == other.Inserted;
        }

        public override bool Equals(object obj)
        {
            return obj is SentinelDiffResult && Equals((SentinelDiffResult)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (Inserted.GetHashCode() * 397) ^ Deleted;
            }
        }
    }

    // 隐藏 TextBox 的哨兵差分纯函数（SP05 定稿：哨兵为两个 U+200B）。
    public static class SentinelDiff
    {
        public const char SentinelChar = '\u200B';
        public const string Sentinel = "\u200B\u200B";

        public static SentinelDiffResult Compute(string current)
        {
            current = current ?? string.Empty;
            int zwsp = 0;
            var inserted = new StringBuilder(current.Length);
            for (int i = 0; i < current.Length; i++)
            {
                char c = current[i];
                if (c == SentinelChar)
                {
                    zwsp++;
                }
                else
                {
                    inserted.Append(c);
                }
            }
            int deleted = Sentinel.Length - zwsp;
            if (deleted < 0)
            {
                deleted = 0;
            }
            return new SentinelDiffResult(inserted.ToString(), deleted);
        }

        public static string Restore()
        {
            return Sentinel;
        }

        public static bool IsRestored(string current)
        {
            return current == Sentinel;
        }
    }

    public sealed class TerminalInputEventArgs : EventArgs
    {
        public TerminalInputEventArgs(byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            Data = data;
        }

        public byte[] Data { get; }
    }
}
