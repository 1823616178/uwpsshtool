using System;
using System.Text;

namespace SshTool.Core.Sftp
{
    // F02：权限位 rwx 九宫格 ↔ 八进制互转（02-UI-DESIGN.md §5.17 权限对话框）。
    // 只处理低 9 位（S_IFMT 类型位与 suid/sgid/sticky 由 native 侧掩掉，
    // 见 native/core/sftp/sftp_session.cpp setStat 的 07777 掩码注释）。
    // 纯函数，可单测。
    public static class PermissionBits
    {
        // 低 9 位掩码 0777。
        public const int ModeMask = 0x1FF;

        // 低 9 位→3 位八进制串（0755→"755"，0644→"644"）。
        public static string ToOctal(int mode)
        {
            int masked = mode & ModeMask;
            char[] digits = new char[3];
            digits[0] = (char)('0' + ((masked >> 6) & 7));
            digits[1] = (char)('0' + ((masked >> 3) & 7));
            digits[2] = (char)('0' + (masked & 7));
            return new string(digits);
        }

        // "755"/"0755"→0755。仅接受 3 位全 0-7，或首位 '0' 的 4 位；
        // 其余（null/空/长度不对/含 8/9/字母）返回 false。
        public static bool TryParseOctal(string text, out int mode)
        {
            mode = 0;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            string digits = text;
            if (digits.Length == 4)
            {
                if (digits[0] != '0')
                {
                    return false;
                }
                digits = digits.Substring(1);
            }
            if (digits.Length != 3)
            {
                return false;
            }
            int value = 0;
            for (int i = 0; i < 3; i++)
            {
                char c = digits[i];
                if (c < '0' || c > '7')
                {
                    return false;
                }
                value = (value << 3) | (c - '0');
            }
            mode = value;
            return true;
        }

        // 低 9 位→"rwxr-xr-x"。
        // 注意：C# 无八进制字面量，以下掩码用十六进制（0400=0x100 … 0001=0x1）。
        public static string ToRwx(int mode)
        {
            int masked = mode & ModeMask;
            var sb = new StringBuilder(9);
            sb.Append((masked & 0x100) != 0 ? 'r' : '-');
            sb.Append((masked & 0x80) != 0 ? 'w' : '-');
            sb.Append((masked & 0x40) != 0 ? 'x' : '-');
            sb.Append((masked & 0x20) != 0 ? 'r' : '-');
            sb.Append((masked & 0x10) != 0 ? 'w' : '-');
            sb.Append((masked & 0x08) != 0 ? 'x' : '-');
            sb.Append((masked & 0x04) != 0 ? 'r' : '-');
            sb.Append((masked & 0x02) != 0 ? 'w' : '-');
            sb.Append((masked & 0x01) != 0 ? 'x' : '-');
            return sb.ToString();
        }

        // "rwxr-xr-x"→0755。必须恰 9 字符且每位合法（r/w/x 或 '-' 在其位）；
        // 其余返回 false。
        public static bool TryParseRwx(string text, out int mode)
        {
            mode = 0;
            if (string.IsNullOrEmpty(text) || text.Length != 9)
            {
                return false;
            }
            char[] expect = { 'r', 'w', 'x', 'r', 'w', 'x', 'r', 'w', 'x' };
            int value = 0;
            for (int i = 0; i < 9; i++)
            {
                char c = text[i];
                if (c == '-')
                {
                    continue;
                }
                if (c != expect[i])
                {
                    return false;
                }
                value |= 1 << (8 - i);
            }
            mode = value;
            return true;
        }
    }
}
