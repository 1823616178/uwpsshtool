using System;
using System.Collections.Generic;

namespace SshTool.Core.Terminal
{
    // W02：一行文本中的一个链接。Start/Length 为字符下标，Url 为可直接打开的地址。
    public struct DetectedLink
    {
        public DetectedLink(int start, int length, string url)
        {
            Start = start;
            Length = length;
            Url = url;
        }

        public int Start { get; }
        public int Length { get; }
        public string Url { get; }
    }

    // W02（01-DESIGN §16.2）：识别 http(s):// 与 www. 开头的 URL。纯函数。
    public static class LinkDetector
    {
        private const string TrailingPunctuation = ".,;:!?";
        private const string Terminators = "<>\"'`";

        public static List<DetectedLink> Find(string text)
        {
            var links = new List<DetectedLink>();
            if (string.IsNullOrEmpty(text))
            {
                return links;
            }
            int i = 0;
            while (i < text.Length)
            {
                int start = NextCandidate(text, i);
                if (start < 0)
                {
                    break;
                }
                int end = start;
                while (end < text.Length && !char.IsWhiteSpace(text[end]) && Terminators.IndexOf(text[end]) < 0)
                {
                    end++;
                }
                int length = TrimTail(text, start, end - start);
                string raw = text.Substring(start, length);
                if (IsUsable(raw))
                {
                    string url = raw.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "http://" + raw : raw;
                    links.Add(new DetectedLink(start, length, url));
                }
                i = Math.Max(end, start + 1);
            }
            return links;
        }

        // 字符下标 index 落在某个链接上时返回它的地址，否则 null。
        public static string UrlAt(string text, int index)
        {
            foreach (DetectedLink link in Find(text))
            {
                if (index >= link.Start && index < link.Start + link.Length)
                {
                    return link.Url;
                }
            }
            return null;
        }

        private static int NextCandidate(string text, int from)
        {
            int best = -1;
            foreach (string prefix in new[] { "https://", "http://", "www." })
            {
                int hit = from;
                while (true)
                {
                    hit = text.IndexOf(prefix, hit, StringComparison.OrdinalIgnoreCase);
                    if (hit < 0)
                    {
                        break;
                    }
                    // 词首才算（"awww.x" 里的 www. 不是链接）。
                    if (hit == 0 || !char.IsLetterOrDigit(text[hit - 1]))
                    {
                        break;
                    }
                    hit++;
                }
                if (hit >= 0 && (best < 0 || hit < best))
                {
                    best = hit;
                }
            }
            return best;
        }

        // 去掉结尾标点与不成对的右括号：`(see https://a.b/c).` → `https://a.b/c`。
        private static int TrimTail(string text, int start, int length)
        {
            while (length > 0)
            {
                char last = text[start + length - 1];
                if (TrailingPunctuation.IndexOf(last) >= 0)
                {
                    length--;
                    continue;
                }
                if (last == ')' || last == ']' || last == '}')
                {
                    char open = last == ')' ? '(' : last == ']' ? '[' : '{';
                    if (Count(text, start, length, open) < Count(text, start, length, last))
                    {
                        length--;
                        continue;
                    }
                }
                break;
            }
            return length;
        }

        private static int Count(string text, int start, int length, char c)
        {
            int n = 0;
            for (int i = start; i < start + length; i++)
            {
                if (text[i] == c)
                {
                    n++;
                }
            }
            return n;
        }

        private static bool IsUsable(string raw)
        {
            if (raw.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            {
                return raw.Length > 4 && raw.IndexOf('.', 4) > 4;
            }
            int schemeEnd = raw.IndexOf("://", StringComparison.Ordinal);
            return schemeEnd >= 0 && raw.Length > schemeEnd + 3 && char.IsLetterOrDigit(raw[schemeEnd + 3]);
        }
    }
}
