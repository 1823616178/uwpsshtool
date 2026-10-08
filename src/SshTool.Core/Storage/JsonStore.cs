using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SshTool.Core.Common;

namespace SshTool.Core.Storage
{
    // 01-DESIGN.md §8.4：LoadAsync → 读文件 → 按 schemaVersion 跑迁移链 → IEntityCodec 解码；
    // SaveAsync 写 *.tmp 再替换（D8 原子写）。损坏文件备份为 *.corrupt-yyyyMMddHHmmss，
    // 以空集合继续并记 LoadWarnings（D05 启动时在主页 Banner 展示）。
    public sealed class JsonStore<T>
    {
        public const int DefaultSchemaVersion = 1;

        private readonly IFileSystem _fs;
        private readonly IEntityCodec<T> _codec;
        private readonly IReadOnlyList<IMigration> _migrations;
        private readonly ILogger _logger;
        private readonly Func<DateTime> _clock;
        private readonly List<string> _loadWarnings = new List<string>();
        // 读失败且原文件也挪不走时置位：此时盘上的文件很可能完好（只是暂时读不了），
        // 若照常保存，「以空集合继续」后的第一次增删改就会把整份数据覆盖成一两条。
        private bool _saveBlocked;

        public JsonStore(
            IFileSystem fs,
            string path,
            IEntityCodec<T> codec,
            IEnumerable<IMigration> migrations = null,
            ILogger logger = null,
            Func<DateTime> clock = null,
            int schemaVersion = DefaultSchemaVersion)
        {
            if (fs == null)
            {
                throw new ArgumentNullException("fs");
            }
            if (codec == null)
            {
                throw new ArgumentNullException("codec");
            }
            _fs = fs;
            Path = path;
            _codec = codec;
            _migrations = migrations == null
                ? new List<IMigration>()
                : new List<IMigration>(migrations);
            _logger = logger;
            _clock = clock ?? (() => DateTime.Now);
            SchemaVersion = schemaVersion;
        }

        public string Path { get; private set; }
        public int SchemaVersion { get; private set; }

        public IEntityCodec<T> Codec
        {
            get { return _codec; }
        }

        public IReadOnlyList<string> LoadWarnings
        {
            get { return _loadWarnings; }
        }

        // false = 上次加载读取失败且原文件未能挪走，SaveAsync 会拒绝写入。
        public bool CanSave
        {
            get { return !_saveBlocked; }
        }

        public async Task<IReadOnlyList<T>> LoadAsync()
        {
            _loadWarnings.Clear();
            _saveBlocked = false;
            if (!await _fs.ExistsAsync(Path).ConfigureAwait(false))
            {
                return new List<T>();
            }

            string text;
            try
            {
                text = await _fs.ReadAllTextAsync(Path).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 读不出内容就没法像其他损坏那样写备份：改为把原文件整个挪到备份名，
                // 挪不动则禁止后续保存（见 _saveBlocked），宁可这次改动落不了盘也不覆盖原数据。
                Warn(Path + " 已损坏（读取失败: " + ex.Message + "），以空集合继续");
                await PreserveUnreadableAsync().ConfigureAwait(false);
                return new List<T>();
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return new List<T>();
            }

            JObject doc;
            try
            {
                doc = JsonText.ParseObject(text);
            }
            catch (Exception ex)
            {
                return await HandleCorruptAsync("JSON 解析失败: " + ex.Message, text).ConfigureAwait(false);
            }

            int version = 0;
            var versionToken = doc["schemaVersion"];
            if (versionToken != null && versionToken.Type != JTokenType.Null)
            {
                if (versionToken.Type != JTokenType.Integer)
                {
                    return await HandleCorruptAsync("schemaVersion 非整数", text).ConfigureAwait(false);
                }
                version = checked((int)(long)versionToken);
            }

            if (version > SchemaVersion)
            {
                // 更新版本写的文件：不迁移，直接解码（未知字段进 Extra 往返保留）。
                Warn("schemaVersion=" + version.ToString(CultureInfo.InvariantCulture)
                    + " 高于当前 " + SchemaVersion.ToString(CultureInfo.InvariantCulture) + "，跳过迁移直接解码");
            }
            else
            {
                while (version < SchemaVersion)
                {
                    IMigration migration = null;
                    foreach (var m in _migrations)
                    {
                        if (m.From == version)
                        {
                            migration = m;
                            break;
                        }
                    }
                    if (migration == null)
                    {
                        return await HandleCorruptAsync(
                            "缺少迁移段 " + version.ToString(CultureInfo.InvariantCulture)
                            + " → " + SchemaVersion.ToString(CultureInfo.InvariantCulture), text).ConfigureAwait(false);
                    }
                    try
                    {
                        migration.Apply(doc);
                    }
                    catch (Exception ex)
                    {
                        return await HandleCorruptAsync("迁移执行失败: " + ex.Message, text).ConfigureAwait(false);
                    }
                    version = migration.To;
                }
            }

            var itemsToken = doc["items"];
            if (itemsToken == null || itemsToken.Type == JTokenType.Null)
            {
                return new List<T>();
            }
            if (itemsToken.Type != JTokenType.Array)
            {
                return await HandleCorruptAsync("items 非数组", text).ConfigureAwait(false);
            }

            var list = new List<T>();
            try
            {
                foreach (var token in (JArray)itemsToken)
                {
                    if (token.Type != JTokenType.Object)
                    {
                        throw new JsonException("item 非对象: " + token.Type);
                    }
                    list.Add(_codec.Decode((JObject)token));
                }
            }
            catch (Exception ex)
            {
                return await HandleCorruptAsync("解码失败: " + ex.Message, text).ConfigureAwait(false);
            }
            return list;
        }

        public async Task SaveAsync(IReadOnlyList<T> items)
        {
            if (_saveBlocked)
            {
                throw new IOException(Path + " 加载时读取失败且无法备份，为免覆盖原数据拒绝写入（重启应用后重试）");
            }
            var root = new JObject();
            root["schemaVersion"] = SchemaVersion;
            var array = new JArray();
            foreach (var item in items)
            {
                array.Add(_codec.Encode(item));
            }
            root["items"] = array;
            string json = root.ToString(Formatting.None);

            string tmp = Path + ".tmp";
            await _fs.WriteAllTextAsync(tmp, json).ConfigureAwait(false);
            await _fs.MoveAndReplaceAsync(tmp, Path).ConfigureAwait(false);
        }

        private async Task<IReadOnlyList<T>> HandleCorruptAsync(string reason, string originalText)
        {
            Warn(Path + " 已损坏（" + reason + "），以空集合继续");
            if (originalText != null)
            {
                string backup = BackupPath();
                try
                {
                    await _fs.WriteAllTextAsync(backup, originalText).ConfigureAwait(false);
                    Warn("已备份到 " + backup);
                }
                catch (Exception ex)
                {
                    Warn("备份失败: " + ex.Message);
                }
            }
            return new List<T>();
        }

        private async Task PreserveUnreadableAsync()
        {
            string backup = BackupPath();
            try
            {
                await _fs.MoveAndReplaceAsync(Path, backup).ConfigureAwait(false);
                Warn("已备份到 " + backup);
            }
            catch (Exception ex)
            {
                _saveBlocked = true;
                Warn("备份失败: " + ex.Message + "，本次运行不再写入该文件");
            }
        }

        private string BackupPath()
        {
            return Path + ".corrupt-" + _clock().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        }

        private void Warn(string message)
        {
            _loadWarnings.Add(message);
            if (_logger != null)
            {
                _logger.Log(LogLevel.Warning, "Storage", message);
            }
        }
    }
}
