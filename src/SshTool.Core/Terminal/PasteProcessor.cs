using System;
using System.Collections.Generic;
using System.Text;

namespace SshTool.Core.Terminal
{
    // 01-DESIGN.md §7.7：粘贴预处理。换行归一 \r，可选 bracketed paste，4 KiB 分块不切断 UTF-8。
    public static class PasteProcessor
    {
        public const int ChunkSize = 4096;
        public const string BracketStart = "\u001b[200~";
        public const string BracketEnd = "\u001b[201~";

        public static string NormalizeNewlines(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r')
                {
                    sb.Append('\r');
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }
                    continue;
                }
                if (c == '\n')
                {
                    sb.Append('\r');
                    continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        public static int LineCount(string normalized)
        {
            if (string.IsNullOrEmpty(normalized))
            {
                return 0;
            }
            int lines = 1;
            for (int i = 0; i < normalized.Length; i++)
            {
                if (normalized[i] == '\r')
                {
                    lines++;
                }
            }
            if (normalized[normalized.Length - 1] == '\r')
            {
                lines--;
            }
            return lines;
        }

        public static bool NeedsMultilineConfirm(string normalized, bool settingEnabled)
        {
            return settingEnabled && LineCount(normalized) > 1;
        }

        public static string WrapBracketed(string normalized, bool enabled)
        {
            normalized = normalized ?? string.Empty;
            if (!enabled)
            {
                return normalized;
            }
            return BracketStart + normalized + BracketEnd;
        }

        public static IReadOnlyList<byte[]> ChunkUtf8(string text, int chunkSize = ChunkSize)
        {
            if (chunkSize < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(chunkSize));
            }
            byte[] all = Encoding.UTF8.GetBytes(text ?? string.Empty);
            if (all.Length == 0)
            {
                return new byte[0][];
            }
            var chunks = new List<byte[]>();
            int offset = 0;
            while (offset < all.Length)
            {
                int take = Math.Min(chunkSize, all.Length - offset);
                while (take > 0 && offset + take < all.Length && IsUtf8Continuation(all[offset + take]))
                {
                    take--;
                }
                if (take <= 0)
                {
                    take = Math.Min(chunkSize, all.Length - offset);
                }
                var chunk = new byte[take];
                Array.Copy(all, offset, chunk, 0, take);
                chunks.Add(chunk);
                offset += take;
            }
            return chunks;
        }

        public static IReadOnlyList<byte[]> Prepare(string text, bool bracketedPaste, bool confirmMultiline,
                                                    out bool needsConfirm)
        {
            string normalized = NormalizeNewlines(text);
            needsConfirm = NeedsMultilineConfirm(normalized, confirmMultiline);
            string wrapped = WrapBracketed(normalized, bracketedPaste);
            return ChunkUtf8(wrapped);
        }

        private static bool IsUtf8Continuation(byte value)
        {
            return (value & 0xC0) == 0x80;
        }
    }
}
