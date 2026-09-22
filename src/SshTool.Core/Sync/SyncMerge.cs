using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SshTool.Core.Storage;
using SshTool.Core.Sync.Protocol;

namespace SshTool.Core.Sync
{
    // 03-SYNC-PROTOCOL.md §8 三方合并，移植桌面端 sync-merge.ts：
    // 实体先经 SyncDocumentWriter 转成规范 JObject 做通用字段合并，合并结果再经
    // SyncDocumentReader 校验转回强类型。冲突只记 entity/id/field/sensitive/kind，不含值。
    // updatedAt 取较大者（固定 ISO 格式字符串的字典序即时间序）。
    public static class SyncMerge
    {
        public static SyncMergeResult Merge(SyncDocumentV1 baseDoc, SyncDocumentV1 local, SyncDocumentV1 remote)
        {
            AssertVersion(baseDoc);
            AssertVersion(local);
            AssertVersion(remote);

            var conflicts = new List<SyncMergeConflict>();
            JObject baseJson = ToCanonicalJson(baseDoc);
            JObject localJson = ToCanonicalJson(local);
            JObject remoteJson = ToCanonicalJson(remote);

            var merged = new JObject();
            merged["schemaVersion"] = SyncConstants.SchemaVersion;
            // 固定格式（yyyy-MM-ddTHH:mm:ss.fffZ）下字典序 == 时间序
            merged["updatedAt"] = string.CompareOrdinal(local.UpdatedAt, remote.UpdatedAt) >= 0
                ? local.UpdatedAt
                : remote.UpdatedAt;
            merged["preferences"] = MergeFields(
                baseJson["preferences"], localJson["preferences"], remoteJson["preferences"],
                SyncMergeEntity.Settings, "preferences", "preferences", conflicts, _ => true);
            merged["servers"] = MergeEntityArray(
                baseJson["servers"], localJson["servers"], remoteJson["servers"],
                SyncMergeEntity.Server,
                item => (string)item["profile"]["id"],
                MergeServer,
                conflicts);
            merged["tunnels"] = MergeEntityArray(
                baseJson["tunnels"], localJson["tunnels"], remoteJson["tunnels"],
                SyncMergeEntity.Tunnel,
                item => (string)item["id"],
                (b, l, r, list) => MergeFields(b, l, r, SyncMergeEntity.Tunnel, (string)l["id"], "", list, _ => false),
                conflicts);
            merged["groups"] = MergeEntityArray(
                baseJson["groups"], localJson["groups"], remoteJson["groups"],
                SyncMergeEntity.Group,
                item => (string)item["id"],
                (b, l, r, list) => MergeFields(b, l, r, SyncMergeEntity.Group, (string)l["id"], "", list, _ => false),
                conflicts);

            // 合并结果转回强类型（Reader 全量校验兜底）
            var document = SyncDocumentReader.Read(merged.ToString(Formatting.None));
            return new SyncMergeResult(document, conflicts);
        }

        private static void AssertVersion(SyncDocumentV1 doc)
        {
            if (doc == null || doc.SchemaVersion != SyncConstants.SchemaVersion)
            {
                throw new SyncDocumentInvalidException("schemaVersion",
                    "不支持的同步文档版本：" + (doc == null ? "<null>" : doc.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        // O07：输入侧改走 WriteUnvalidated——base/local/remote 都是已校验或现建的
        // 文档，Write 里的 Validate 是三次全量重复遍历。合并结果仍在 Merge 末尾
        // 经 SyncDocumentReader.Read 全量校验（R8 兜底）。
        //
        // 这里保留「Write → Parse」而不另写一个「模型 → JObject」直转：直转会让
        // schema 出现第二个事实来源，字段一旦漏掉就是静默的同步数据丢失，代价
        // 远大于省下的这点 CPU。
        private static JObject ToCanonicalJson(SyncDocumentV1 doc)
        {
            return JsonText.ParseObject(SyncDocumentWriter.WriteUnvalidated(doc));
        }

        // §8 MergeValue：local 没变 → remote；remote 没变或两边相等 → local；否则记冲突暂取 local。
        private static JToken MergeValue(
            JToken baseValue, JToken localValue, JToken remoteValue,
            SyncMergeEntity entity, string id, string field, bool sensitive,
            List<SyncMergeConflict> conflicts)
        {
            bool localChanged = !TokenEquals(localValue, baseValue);
            bool remoteChanged = !TokenEquals(remoteValue, baseValue);
            if (!localChanged)
            {
                return CloneToken(remoteValue);
            }
            if (!remoteChanged || TokenEquals(localValue, remoteValue))
            {
                return CloneToken(localValue);
            }
            conflicts.Add(new SyncMergeConflict
            {
                Entity = entity,
                Id = id,
                Field = field,
                Sensitive = sensitive,
                Kind = SyncMergeKind.Field
            });
            return CloneToken(localValue);
        }

        // §8 MergeFields：对三者键并集逐键合并；合并结果为「不存在」的键不写入
        //（等价桌面端 JSON.stringify 丢弃 undefined 键）。
        private static JObject MergeFields(
            JToken baseObj, JToken localObj, JToken remoteObj,
            SyncMergeEntity entity, string id, string prefix,
            List<SyncMergeConflict> conflicts, Func<string, bool> isSensitive)
        {
            var keys = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in new[] { baseObj, localObj, remoteObj })
            {
                if (source is JObject o)
                {
                    foreach (var p in o.Properties())
                    {
                        if (seen.Add(p.Name))
                        {
                            keys.Add(p.Name);
                        }
                    }
                }
            }

            var output = new JObject();
            foreach (var key in keys)
            {
                string field = prefix.Length == 0 ? key : prefix + "." + key;
                JToken mergedValue = MergeValue(
                    ValueOf(baseObj, key), ValueOf(localObj, key), ValueOf(remoteObj, key),
                    entity, id, field, isSensitive(field), conflicts);
                if (mergedValue != null)
                {
                    output[key] = mergedValue;
                }
            }
            return output;
        }

        private static JObject MergeServer(
            JToken baseItem, JToken localItem, JToken remoteItem,
            List<SyncMergeConflict> conflicts)
        {
            string id = (string)localItem["profile"]["id"];
            var profile = MergeFields(
                baseItem["profile"], localItem["profile"], remoteItem["profile"],
                SyncMergeEntity.Server, id, "profile", conflicts,
                field => field == "profile.hostFingerprint");
            var secrets = MergeFields(
                ValueOf(baseItem, "secrets"), ValueOf(localItem, "secrets"), ValueOf(remoteItem, "secrets"),
                SyncMergeEntity.Server, id, "secrets", conflicts,
                _ => true);

            var result = new JObject();
            result["profile"] = profile;
            if (secrets.HasValues)
            {
                result["secrets"] = secrets;
            }
            return result;
        }

        private static JToken MergeAdded(
            JToken localItem, JToken remoteItem,
            SyncMergeEntity entity, string id,
            List<SyncMergeConflict> conflicts)
        {
            if (!TokenEquals(localItem, remoteItem))
            {
                conflicts.Add(new SyncMergeConflict
                {
                    Entity = entity,
                    Id = id,
                    Field = "*",
                    Sensitive = entity == SyncMergeEntity.Server,
                    Kind = SyncMergeKind.AddAdd
                });
            }
            return CloneToken(localItem);
        }

        private static JArray MergeEntityArray(
            JToken baseItems, JToken localItems, JToken remoteItems,
            SyncMergeEntity entity,
            Func<JToken, string> idOf,
            Func<JToken, JToken, JToken, List<SyncMergeConflict>, JObject> mergeExisting,
            List<SyncMergeConflict> conflicts)
        {
            var baseMap = IndexById(baseItems, idOf);
            var localMap = IndexById(localItems, idOf);
            var remoteMap = IndexById(remoteItems, idOf);

            var ids = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var map in new[] { baseMap, localMap, remoteMap })
            {
                ids.UnionWith(map.Keys);
            }

            var result = new JArray();
            foreach (var id in ids)
            {
                JToken b, l, r;
                bool hasBase = baseMap.TryGetValue(id, out b);
                bool hasLocal = localMap.TryGetValue(id, out l);
                bool hasRemote = remoteMap.TryGetValue(id, out r);

                if (!hasBase)
                {
                    if (hasLocal && hasRemote)
                    {
                        result.Add(MergeAdded(l, r, entity, id, conflicts));
                    }
                    else
                    {
                        result.Add(CloneToken(hasLocal ? l : r));
                    }
                    continue;
                }
                if (!hasLocal && !hasRemote)
                {
                    continue; // 两边都删 → 删除
                }
                if (!hasLocal)
                {
                    if (TokenEquals(r, b))
                    {
                        continue; // 本地删除、远端没动 → 删除
                    }
                    // 本地删除 vs 远端修改：不保留（待用户决定）
                    conflicts.Add(DeleteModify(entity, id));
                    continue;
                }
                if (!hasRemote)
                {
                    if (TokenEquals(l, b))
                    {
                        continue;
                    }
                    // 远端删除 vs 本地修改：保留 local（待用户决定）
                    conflicts.Add(DeleteModify(entity, id));
                    result.Add(CloneToken(l));
                    continue;
                }
                result.Add(mergeExisting(b, l, r, conflicts));
            }
            return result;
        }

        private static SyncMergeConflict DeleteModify(SyncMergeEntity entity, string id)
        {
            return new SyncMergeConflict
            {
                Entity = entity,
                Id = id,
                Field = "*",
                Sensitive = entity == SyncMergeEntity.Server,
                Kind = SyncMergeKind.DeleteModify
            };
        }

        private static Dictionary<string, JToken> IndexById(JToken items, Func<JToken, string> idOf)
        {
            var map = new Dictionary<string, JToken>(StringComparer.Ordinal);
            if (items is JArray array)
            {
                foreach (var item in array)
                {
                    map[idOf(item)] = item;
                }
            }
            return map;
        }

        private static JToken ValueOf(JToken obj, string key)
        {
            var o = obj as JObject;
            return o == null ? null : o[key];
        }

        private static bool TokenEquals(JToken a, JToken b)
        {
            if (ReferenceEquals(a, b))
            {
                return true;
            }
            if (a == null || b == null)
            {
                return false;
            }
            return JToken.DeepEquals(a, b);
        }

        private static JToken CloneToken(JToken token)
        {
            return token == null ? null : token.DeepClone();
        }
    }
}
