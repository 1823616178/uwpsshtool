using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SshTool.Core.Storage;

namespace SshTool.Core.Sessions
{
    public sealed class SessionSnapshot
    {
        public List<string> HostIds { get; set; }
        public string PaneLayoutJson { get; set; }

        public SessionSnapshot()
        {
            HostIds = new List<string>();
            PaneLayoutJson = string.Empty;
        }
    }

    // 01-DESIGN.md §10：Suspending 写 state/sessions.json；冷启动恢复卡片。
    public sealed class SessionSnapshotStore
    {
        public const string Path = "state/sessions.json";
        private readonly IFileSystem _fs;

        public SessionSnapshotStore(IFileSystem fs)
        {
            if (fs == null)
            {
                throw new ArgumentNullException("fs");
            }
            _fs = fs;
        }

        public async Task SaveAsync(SessionSnapshot snapshot)
        {
            if (snapshot == null)
            {
                snapshot = new SessionSnapshot();
            }
            var o = new JObject();
            var ids = new JArray();
            if (snapshot.HostIds != null)
            {
                for (int i = 0; i < snapshot.HostIds.Count; i++)
                {
                    if (!string.IsNullOrEmpty(snapshot.HostIds[i]))
                    {
                        ids.Add(snapshot.HostIds[i]);
                    }
                }
            }
            o["hostIds"] = ids;
            o["paneLayout"] = snapshot.PaneLayoutJson ?? string.Empty;
            o["savedAt"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            await _fs.WriteAllTextAsync(Path, o.ToString(Formatting.None)).ConfigureAwait(false);
        }

        public async Task<SessionSnapshot> LoadAsync(ISet<string> existingHostIds)
        {
            var result = new SessionSnapshot();
            if (!await _fs.ExistsAsync(Path).ConfigureAwait(false))
            {
                return result;
            }
            string text = await _fs.ReadAllTextAsync(Path).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                return result;
            }
            JObject o;
            try
            {
                o = JsonText.ParseObject(text);
            }
            catch (Exception)
            {
                return result;
            }
            JToken idsToken = o["hostIds"];
            JArray ids = idsToken as JArray;
            if (ids != null)
            {
                for (int i = 0; i < ids.Count; i++)
                {
                    string id = (string)ids[i];
                    if (string.IsNullOrEmpty(id))
                    {
                        continue;
                    }
                    if (existingHostIds != null && !existingHostIds.Contains(id))
                    {
                        continue;
                    }
                    result.HostIds.Add(id);
                }
            }
            JToken layout = o["paneLayout"];
            result.PaneLayoutJson = layout == null ? string.Empty : (string)layout;
            return result;
        }
    }
}
