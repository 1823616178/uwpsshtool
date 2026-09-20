using System;
using System.Collections.Generic;

namespace SshTool.Core.Sftp
{
    // F02：远端路径规范化（SFTP 路径恒用 '/'，见 01-DESIGN.md §11.1）。
    // 纯函数，可单测。所有 ISftpClient 入口先经 Normalize 再下发 native。
    public static class RemotePath
    {
        public const string Root = "/";

        // 规范化：'\\'→'/'、合并连续 '/'、消解 '.'、'..'（超根 clamp 到根）、
        // 去尾 '/'（根除外）、空/null/全空白→根。绝不抛异常。
        public static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return Root;
            }
            string slashed = path.Replace('\\', '/');
            string[] parts = slashed.Split('/');
            var stack = new List<string>(parts.Length);
            foreach (string part in parts)
            {
                if (part.Length == 0 || part == ".")
                {
                    continue;
                }
                if (part == "..")
                {
                    if (stack.Count > 0)
                    {
                        stack.RemoveAt(stack.Count - 1);
                    }
                    continue;
                }
                stack.Add(part);
            }
            if (stack.Count == 0)
            {
                return Root;
            }
            return "/" + string.Join("/", stack);
        }

        // 目录与名拼接（name 可含子路径，整体再规范化）。
        public static string Combine(string dir, string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return Normalize(dir);
            }
            if (string.IsNullOrEmpty(dir))
            {
                return Normalize(name);
            }
            return Normalize(dir + "/" + name);
        }

        // 最后一段（根→空串）。
        public static string GetFileName(string path)
        {
            string normalized = Normalize(path);
            if (normalized == Root)
            {
                return string.Empty;
            }
            int slash = normalized.LastIndexOf('/');
            return normalized.Substring(slash + 1);
        }

        // 父目录（根的父→根）。
        public static string GetDirectoryName(string path)
        {
            string normalized = Normalize(path);
            if (normalized == Root)
            {
                return Root;
            }
            int slash = normalized.LastIndexOf('/');
            if (slash <= 0)
            {
                return Root;
            }
            return normalized.Substring(0, slash);
        }

        public static bool IsAbsolute(string path)
        {
            return !string.IsNullOrEmpty(path) && path[0] == '/';
        }
    }
}
