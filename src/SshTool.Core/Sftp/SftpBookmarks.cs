using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SshTool.Core.Storage;

namespace SshTool.Core.Sftp
{
    // W05（01-DESIGN §16.5）：SFTP 路径书签，存本机设置键 sftpBookmarks（JSON：hostId → 路径数组）。
    // 纯函数：输入旧 JSON，输出新 JSON；坏 JSON 当空表处理，不抛。
    public static class SftpBookmarks
    {
        public const int MaxPerHost = 20;

        public static IReadOnlyList<string> Get(string json, string hostId)
        {
            var list = new List<string>();
            JObject root = Parse(json);
            JArray paths = string.IsNullOrEmpty(hostId) ? null : root[hostId] as JArray;
            if (paths == null)
            {
                return list;
            }
            foreach (JToken token in paths)
            {
                if (token.Type == JTokenType.String)
                {
                    list.Add((string)token);
                }
            }
            return list;
        }

        // 已存在则移到最前；超过上限丢最旧的。
        public static string Add(string json, string hostId, string path)
        {
            if (string.IsNullOrEmpty(hostId) || string.IsNullOrWhiteSpace(path))
            {
                return Normalize(json);
            }
            var list = new List<string>(Get(json, hostId));
            list.RemoveAll(p => string.Equals(p, path, StringComparison.Ordinal));
            list.Insert(0, path);
            if (list.Count > MaxPerHost)
            {
                list.RemoveRange(MaxPerHost, list.Count - MaxPerHost);
            }
            return Write(json, hostId, list);
        }

        public static string Remove(string json, string hostId, string path)
        {
            if (string.IsNullOrEmpty(hostId))
            {
                return Normalize(json);
            }
            var list = new List<string>(Get(json, hostId));
            list.RemoveAll(p => string.Equals(p, path, StringComparison.Ordinal));
            return Write(json, hostId, list);
        }

        public static bool Contains(string json, string hostId, string path)
        {
            foreach (string p in Get(json, hostId))
            {
                if (string.Equals(p, path, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static string Write(string json, string hostId, List<string> paths)
        {
            JObject root = Parse(json);
            if (paths.Count == 0)
            {
                root.Remove(hostId);
            }
            else
            {
                root[hostId] = new JArray(paths.ToArray());
            }
            return root.ToString(Formatting.None);
        }

        private static string Normalize(string json)
        {
            return Parse(json).ToString(Formatting.None);
        }

        private static JObject Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new JObject();
            }
            try
            {
                return JsonText.ParseObject(json.Trim()) ?? new JObject();
            }
            catch (Exception)
            {
                return new JObject();
            }
        }
    }
}
