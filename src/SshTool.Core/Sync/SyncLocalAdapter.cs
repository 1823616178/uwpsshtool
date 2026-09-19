using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SshTool.Core.Common;
using SshTool.Core.Models;
using SshTool.Core.Storage;
using SshTool.Core.Storage.Repositories;
using SshTool.Core.Sync.Protocol;

namespace SshTool.Core.Sync
{
    // 03-SYNC-PROTOCOL.md §9 私钥同步在 S15 落地。S10 只定义接口 + 空实现：
    // NullPrivateKeyInspector 下 Build 永不输出 privateKey 段、Apply 跳过 KeyEntry 创建，不阻塞。
    public sealed class InspectedPrivateKey
    {
        public string Base64 { get; set; }
        public string Format { get; set; }      // openssh / pem（PrivateKeyFormat 常量）
        public string Fingerprint { get; set; } // SHA256:<43 位标准 base64 无填充>
    }

    public interface IPrivateKeyInspector
    {
        // 出站：私钥原文（UTF-8 文本）+ 短语（可为 null）→ 可同步元数据；
        // 返回 null 表示无法解析，调用方跳过该主机私钥段。
        InspectedPrivateKey Inspect(string privateKeyText, string passphrase);

        // 入站：校验远端 secrets 私钥段（含 §4.1 结构之外的指纹复算）；
        // 失败抛 SyncApplyException（整份不应用）；返回私钥原文，返回 null 表示跳过（S15 前）。
        string DecodeAndVerify(ServerSecrets secrets, string serverId);
    }

    public sealed class NullPrivateKeyInspector : IPrivateKeyInspector
    {
        public static readonly NullPrivateKeyInspector Instance = new NullPrivateKeyInspector();

        private NullPrivateKeyInspector()
        {
        }

        public InspectedPrivateKey Inspect(string privateKeyText, string passphrase)
        {
            return null;
        }

        public string DecodeAndVerify(ServerSecrets secrets, string serverId)
        {
            return null;
        }
    }

    // 03-SYNC-PROTOCOL.md §5 本地模型 ↔ SyncDocumentV1 映射。
    // 上行出站文档只经 SyncDocumentWriter 序列化（固定键序/排序，见 Writer/SameContent），
    // 下行入站文档由调用方经 SyncDocumentReader 校验后传入（此处再做 Validator 防御性复检）。
    // 日志只记相位与数量，不记密码/短语/私钥/明文/密文。
    public sealed class SyncLocalAdapter : ISyncLocalPort
    {
        private const string Tag = "SyncLocalAdapter";
        private const string UpdatedAtFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";

        private readonly HostRepository _hosts;
        private readonly GroupRepository _groups;
        private readonly TunnelRepository _tunnels;
        private readonly KeyRepository _keys;
        private readonly ISecretStore _secrets;
        private readonly ITunnelBusyProbe _busyProbe;
        private readonly IPrivateKeyInspector _inspector;
        private readonly ILogger _log;
        private readonly Func<DateTime> _utcNow;

        public SyncLocalAdapter(
            HostRepository hosts,
            GroupRepository groups,
            TunnelRepository tunnels,
            KeyRepository keys,
            ISecretStore secrets,
            ITunnelBusyProbe busyProbe = null,
            IPrivateKeyInspector keyInspector = null,
            ILogger logger = null,
            Func<DateTime> utcNow = null)
        {
            if (hosts == null) throw new ArgumentNullException("hosts");
            if (groups == null) throw new ArgumentNullException("groups");
            if (tunnels == null) throw new ArgumentNullException("tunnels");
            if (keys == null) throw new ArgumentNullException("keys");
            if (secrets == null) throw new ArgumentNullException("secrets");
            _hosts = hosts;
            _groups = groups;
            _tunnels = tunnels;
            _keys = keys;
            _secrets = secrets;
            _busyProbe = busyProbe;
            _inspector = keyInspector ?? NullPrivateKeyInspector.Instance;
            _log = logger;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        // §5.1 构建本地文档（上行）。preferences 取自 VaultCache（syncPasswords/syncPrivateKeys）。
        public async Task<SyncDocumentV1> BuildLocalDocumentAsync(SyncPreferencesV1 preferences)
        {
            if (preferences == null) throw new ArgumentNullException("preferences");

            var hosts = await _hosts.GetAllAsync().ConfigureAwait(false);
            var groups = await _groups.GetAllAsync().ConfigureAwait(false);
            var tunnels = await _tunnels.GetAllAsync().ConfigureAwait(false);

            var groupIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in groups)
            {
                if (g.Id != null) groupIds.Add(g.Id);
            }
            var hostIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var h in hosts)
            {
                if (h.Id != null) hostIds.Add(h.Id);
            }

            var doc = new SyncDocumentV1();
            doc.UpdatedAt = _utcNow().ToUniversalTime().ToString(UpdatedAtFormat, CultureInfo.InvariantCulture);
            doc.Preferences = new SyncPreferencesV1
            {
                SyncPasswords = preferences.SyncPasswords,
                SyncPrivateKeys = preferences.SyncPrivateKeys
            };

            foreach (var h in hosts)
            {
                doc.Servers.Add(await BuildServerAsync(h, preferences).ConfigureAwait(false));
            }

            int droppedTunnels = 0;
            int nulledTunnelGroups = 0;
            foreach (var t in tunnels)
            {
                // 上行前兜底过滤（本地正常不应出现，仓库层已阻止）：serverId 悬空则丢弃。
                if (string.IsNullOrEmpty(t.ServerId) || !hostIds.Contains(t.ServerId))
                {
                    droppedTunnels++;
                    continue;
                }
                string groupId = t.GroupId;
                if (string.IsNullOrEmpty(groupId))
                {
                    groupId = null;
                }
                else if (!groupIds.Contains(groupId))
                {
                    groupId = null;
                    nulledTunnelGroups++;
                }
                // 隧道除 autoStart 外全部进文档（relay 原样，见 §5.1）。
                doc.Tunnels.Add(new TunnelRecord
                {
                    Id = t.Id,
                    Name = t.Name,
                    ServerId = t.ServerId,
                    GroupId = groupId,
                    Type = t.Type,
                    ListenHost = t.ListenHost ?? "",
                    ListenPort = t.ListenPort,
                    DestHost = t.DestHost ?? "",
                    DestPort = t.DestPort,
                    DestServerId = t.DestServerId ?? "",
                    AutoReconnect = t.AutoReconnect,
                    Enabled = t.Enabled
                });
            }

            foreach (var g in groups)
            {
                doc.Groups.Add(new GroupRecord { Id = g.Id, Name = g.Name, Color = g.Color });
            }

            // 零偏差门禁：出站文档必须通过 Writer（固定键序/排序 + 2 MiB 上限，失败即抛）。
            SyncDocumentWriter.Write(doc);

            if (_log != null)
            {
                if (droppedTunnels > 0 || nulledTunnelGroups > 0)
                {
                    _log.Log(LogLevel.Warning, Tag, "Build 过滤悬空引用 tunnels=" + droppedTunnels
                        + " nulledGroups=" + nulledTunnelGroups);
                }
                _log.Log(LogLevel.Info, Tag, "Build servers=" + doc.Servers.Count
                    + " tunnels=" + doc.Tunnels.Count + " groups=" + doc.Groups.Count);
            }
            return doc;
        }

        private async Task<ServerRecord> BuildServerAsync(Host h, SyncPreferencesV1 preferences)
        {
            var record = new ServerRecord
            {
                Profile = new PortableServerProfile
                {
                    Id = h.Id,
                    Name = h.Name,
                    Host = h.HostName,
                    Port = h.Port,
                    Username = h.Username,
                    AuthType = h.AuthType,
                    HostFingerprint = h.HostFingerprint ?? "",
                    Keepalive = h.Keepalive
                },
                Secrets = null
            };

            // 短语解析需要短语，但只有 syncPasswords 开启时才输出（§5.1）。
            bool needPassphrase = preferences.SyncPasswords
                || (preferences.SyncPrivateKeys && h.AuthType == AuthType.Key && !string.IsNullOrEmpty(h.KeyId));
            string passphrase = null;
            if (needPassphrase)
            {
                if (!string.IsNullOrEmpty(h.KeyId))
                {
                    passphrase = await _secrets.GetAsync(SecretKeys.KeyPassphrase(h.KeyId)).ConfigureAwait(false);
                }
                if (string.IsNullOrEmpty(passphrase))
                {
                    passphrase = await _secrets.GetAsync(SecretKeys.HostPassphrase(h.Id)).ConfigureAwait(false);
                }
            }

            ServerSecrets secrets = null;
            if (preferences.SyncPasswords)
            {
                string password = null;
                if (h.AuthType == AuthType.Password)
                {
                    password = await _secrets.GetAsync(SecretKeys.HostPassword(h.Id)).ConfigureAwait(false);
                }
                if (!string.IsNullOrEmpty(password) || !string.IsNullOrEmpty(passphrase))
                {
                    secrets = new ServerSecrets();
                    if (!string.IsNullOrEmpty(password)) secrets.Password = password;
                    if (!string.IsNullOrEmpty(passphrase)) secrets.Passphrase = passphrase;
                }
            }

            // 私钥段 S15 落地：Null 检查器返回 null 即跳过（S10 前此项不输出，见 §5.1）。
            if (preferences.SyncPrivateKeys && h.AuthType == AuthType.Key && !string.IsNullOrEmpty(h.KeyId))
            {
                string privateText = await _secrets.GetAsync(SecretKeys.KeyPrivate(h.KeyId)).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(privateText))
                {
                    InspectedPrivateKey inspected = _inspector.Inspect(privateText, passphrase);
                    if (inspected != null
                        && !string.IsNullOrEmpty(inspected.Base64)
                        && !string.IsNullOrEmpty(inspected.Format)
                        && !string.IsNullOrEmpty(inspected.Fingerprint))
                    {
                        if (secrets == null) secrets = new ServerSecrets();
                        secrets.PrivateKey = inspected.Base64;
                        secrets.PrivateKeyEncoding = PrivateKeyFormat.EncodingBase64;
                        secrets.PrivateKeyFormat = inspected.Format;
                        secrets.PrivateKeyFingerprint = inspected.Fingerprint;
                    }
                }
            }

            record.Secrets = secrets;
            return record;
        }

        // §5.2 应用远端文档（下行）。任一道保护触发即抛 SyncApplyException，整份不应用。
        public async Task ApplyDocumentAsync(SyncDocumentV1 doc)
        {
            if (doc == null) throw new ArgumentNullException("doc");
            SyncDocumentValidator.Validate(doc);

            var localHosts = await _hosts.GetAllAsync().ConfigureAwait(false);
            var localTunnels = await _tunnels.GetAllAsync().ConfigureAwait(false);
            var localGroups = await _groups.GetAllAsync().ConfigureAwait(false);

            var remoteById = new Dictionary<string, ServerRecord>(StringComparer.Ordinal);
            foreach (var s in doc.Servers)
            {
                remoteById[s.Profile.Id] = s;
            }
            var remoteTunnels = new Dictionary<string, TunnelRecord>(StringComparer.Ordinal);
            foreach (var t in doc.Tunnels)
            {
                remoteTunnels[t.Id] = t;
            }

            // 保护 1：主机指纹变化拒绝（本机非空且远端同 id 指纹不同）。
            foreach (var h in localHosts)
            {
                if (string.IsNullOrEmpty(h.HostFingerprint)) continue;
                ServerRecord remote;
                if (remoteById.TryGetValue(h.Id, out remote))
                {
                    string remoteFingerprint = remote.Profile.HostFingerprint ?? "";
                    if (!string.Equals(h.HostFingerprint, remoteFingerprint, StringComparison.Ordinal))
                    {
                        if (_log != null) _log.Log(LogLevel.Warning, Tag, "Apply 拒绝：主机指纹变化");
                        throw new SyncApplyException(SyncApplyFailure.HostFingerprintChanged,
                            "服务器「" + h.Name + "」主机指纹发生变化，需要手动确认");
                    }
                }
            }

            // 保护 2：运行中隧道涉及连接变更拒绝（远端删除它 / 连接字段变化 / 其主机连接字段变化）。
            var localHostById = new Dictionary<string, Host>(StringComparer.Ordinal);
            foreach (var h in localHosts)
            {
                localHostById[h.Id] = h;
            }
            if (_busyProbe != null)
            {
                foreach (var t in localTunnels)
                {
                    if (!_busyProbe.IsBusy(t.Id)) continue;
                    if (TunnelConnectionChanged(t, remoteTunnels) || TunnelServerChanged(t, localHostById, remoteById))
                    {
                        if (_log != null) _log.Log(LogLevel.Warning, Tag, "Apply 拒绝：运行中隧道变更");
                        throw new SyncApplyException(SyncApplyFailure.TunnelBusy,
                            "运行中的隧道「" + t.Name + "」涉及远端连接变更，请先停止后重试");
                    }
                }
            }

            var localGroupById = new Dictionary<string, HostGroup>(StringComparer.Ordinal);
            foreach (var g in localGroups)
            {
                localGroupById[g.Id] = g;
            }
            var localTunnelById = new Dictionary<string, Tunnel>(StringComparer.Ordinal);
            foreach (var t in localTunnels)
            {
                localTunnelById[t.Id] = t;
            }
            var remoteGroupIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in doc.Groups)
            {
                remoteGroupIds.Add(g.Id);
            }

            // 主机：远端为准重建；同 id 覆盖 8 个 ☁ 字段并保留全部 🏠；新 id 取 🏠 默认值。
            var newHosts = new List<Host>(doc.Servers.Count);
            foreach (var s in doc.Servers)
            {
                Host lh;
                if (localHostById.TryGetValue(s.Profile.Id, out lh))
                {
                    Host merged = lh.Clone();
                    merged.Name = s.Profile.Name;
                    merged.HostName = s.Profile.Host;
                    merged.Port = s.Profile.Port;
                    merged.Username = s.Profile.Username;
                    merged.AuthType = s.Profile.AuthType;
                    merged.HostFingerprint = s.Profile.HostFingerprint;
                    merged.Keepalive = s.Profile.Keepalive;
                    if (merged.GroupId != null && !remoteGroupIds.Contains(merged.GroupId))
                    {
                        merged.GroupId = null;
                    }
                    newHosts.Add(merged);
                }
                else
                {
                    Host created = Defaults.NewHost();
                    created.Id = s.Profile.Id;
                    created.Name = s.Profile.Name;
                    created.HostName = s.Profile.Host;
                    created.Port = s.Profile.Port;
                    created.Username = s.Profile.Username;
                    created.AuthType = s.Profile.AuthType;
                    created.HostFingerprint = s.Profile.HostFingerprint;
                    created.Keepalive = s.Profile.Keepalive;
                    created.GroupId = null;
                    newHosts.Add(created);
                }

                // 私钥入站（S10 起结构已由 Reader/Validator 校验；指纹复算与 KeyEntry 落地在 S15）。
                if (s.Secrets != null && s.Secrets.PrivateKey != null)
                {
                    string decoded = _inspector.DecodeAndVerify(s.Secrets, s.Profile.Id);
                    if (decoded != null)
                    {
                        await AssignSyncedKeyAsync(newHosts[newHosts.Count - 1], s.Secrets, decoded).ConfigureAwait(false);
                    }
                }
            }

            // jumpHostId 指向被删主机的置 null（复用 ConfigService 规则；隧道随远端集合整体替换）。
            var surviving = new HashSet<string>(StringComparer.Ordinal);
            foreach (var h in newHosts)
            {
                surviving.Add(h.Id);
            }
            foreach (var h in newHosts)
            {
                if (h.JumpHostId != null && !surviving.Contains(h.JumpHostId))
                {
                    h.JumpHostId = null;
                }
            }

            // 隧道：远端为准，保留本机 autoStart（relay 原样保存）；分组：保留 order/collapsed。
            var newTunnels = new List<Tunnel>(doc.Tunnels.Count);
            foreach (var r in doc.Tunnels)
            {
                var nt = new Tunnel
                {
                    Id = r.Id,
                    Name = r.Name,
                    ServerId = r.ServerId,
                    GroupId = r.GroupId,
                    Type = r.Type,
                    ListenHost = r.ListenHost,
                    ListenPort = r.ListenPort,
                    DestHost = r.DestHost,
                    DestPort = r.DestPort,
                    DestServerId = r.DestServerId,
                    AutoReconnect = r.AutoReconnect,
                    Enabled = r.Enabled,
                    AutoStart = false
                };
                Tunnel lt;
                if (localTunnelById.TryGetValue(r.Id, out lt))
                {
                    nt.AutoStart = lt.AutoStart;
                    nt.Extra = lt.Extra == null ? null : (JObject)lt.Extra.DeepClone();
                }
                newTunnels.Add(nt);
            }

            var newGroups = new List<HostGroup>(doc.Groups.Count);
            foreach (var g in doc.Groups)
            {
                var ng = new HostGroup
                {
                    Id = g.Id,
                    Name = g.Name,
                    Color = g.Color,
                    Order = 0,
                    Collapsed = false
                };
                HostGroup lg;
                if (localGroupById.TryGetValue(g.Id, out lg))
                {
                    ng.Order = lg.Order;
                    ng.Collapsed = lg.Collapsed;
                    ng.Extra = lg.Extra == null ? null : (JObject)lg.Extra.DeepClone();
                }
                newGroups.Add(ng);
            }

            // 下行写入标记 ChangeOrigin.Sync（不触发同步标脏，避免上传回环，见 §5.2/踩坑 13）。
            await _hosts.ReplaceAllAsync(newHosts, ChangeOrigin.Sync).ConfigureAwait(false);
            await _groups.ReplaceAllAsync(newGroups, ChangeOrigin.Sync).ConfigureAwait(false);
            await _tunnels.ReplaceAllAsync(newTunnels, ChangeOrigin.Sync).ConfigureAwait(false);

            // 凭据「出现即覆盖、缺失即保留」；本机有而远端无的主机清其 host: 凭据（复用 ConfigService 级联）。
            foreach (var s in doc.Servers)
            {
                var sec = s.Secrets;
                if (sec == null) continue;
                if (sec.Password != null)
                {
                    string key = SecretKeys.HostPassword(s.Profile.Id);
                    if (sec.Password.Length == 0) await _secrets.RemoveAsync(key).ConfigureAwait(false);
                    else await _secrets.SetAsync(key, sec.Password).ConfigureAwait(false);
                }
                if (sec.Passphrase != null)
                {
                    string key = SecretKeys.HostPassphrase(s.Profile.Id);
                    if (sec.Passphrase.Length == 0) await _secrets.RemoveAsync(key).ConfigureAwait(false);
                    else await _secrets.SetAsync(key, sec.Passphrase).ConfigureAwait(false);
                }
            }
            foreach (var h in localHosts)
            {
                if (!remoteById.ContainsKey(h.Id))
                {
                    await _secrets.RemoveByPrefixAsync(SecretKeys.HostPrefix(h.Id)).ConfigureAwait(false);
                }
            }

            if (_log != null)
            {
                _log.Log(LogLevel.Info, Tag, "Apply servers=" + newHosts.Count
                    + " tunnels=" + newTunnels.Count + " groups=" + newGroups.Count);
            }
        }

        private static bool TunnelConnectionChanged(Tunnel local, Dictionary<string, TunnelRecord> remoteTunnels)
        {
            TunnelRecord r;
            if (!remoteTunnels.TryGetValue(local.Id, out r)) return true;
            return !string.Equals(r.ServerId, local.ServerId, StringComparison.Ordinal)
                || r.Type != local.Type
                || !string.Equals(r.ListenHost ?? "", local.ListenHost ?? "", StringComparison.Ordinal)
                || r.ListenPort != local.ListenPort
                || !string.Equals(r.DestHost ?? "", local.DestHost ?? "", StringComparison.Ordinal)
                || r.DestPort != local.DestPort
                || !string.Equals(r.DestServerId ?? "", local.DestServerId ?? "", StringComparison.Ordinal);
        }

        private static bool TunnelServerChanged(
            Tunnel local,
            Dictionary<string, Host> localHostById,
            Dictionary<string, ServerRecord> remoteById)
        {
            Host server;
            if (!localHostById.TryGetValue(local.ServerId, out server)) return false;
            ServerRecord remote;
            if (!remoteById.TryGetValue(server.Id, out remote)) return true;
            var p = remote.Profile;
            return !string.Equals(p.Host ?? "", server.HostName ?? "", StringComparison.Ordinal)
                || p.Port != server.Port
                || !string.Equals(p.Username ?? "", server.Username ?? "", StringComparison.Ordinal)
                || p.AuthType != server.AuthType;
        }

        // 入站私钥落地（S15 前仅 Null 检查器跳过；自定义检查器返回原文时按指纹去重建 KeyEntry）。
        private async Task AssignSyncedKeyAsync(Host host, ServerSecrets secrets, string privateText)
        {
            var keys = await _keys.GetAllAsync().ConfigureAwait(false);
            foreach (var k in keys)
            {
                if (string.Equals(k.FingerprintSha256, secrets.PrivateKeyFingerprint, StringComparison.Ordinal))
                {
                    host.KeyId = k.Id;
                    return;
                }
            }
            var entry = new KeyEntry
            {
                Id = IdGenerator.NewId(),
                Name = (host.Name ?? "") + " (synced)",
                KeyType = "",
                Bits = 0,
                Format = secrets.PrivateKeyFormat,
                Encrypted = secrets.Passphrase != null,
                PublicKeyOpenSsh = null,
                FingerprintSha256 = secrets.PrivateKeyFingerprint,
                CreatedAt = _utcNow().ToUniversalTime().ToString(UpdatedAtFormat, CultureInfo.InvariantCulture),
                Comment = null
            };
            await _keys.AddAsync(entry, ChangeOrigin.Sync).ConfigureAwait(false);
            await _secrets.SetAsync(SecretKeys.KeyPrivate(entry.Id), privateText).ConfigureAwait(false);
            host.KeyId = entry.Id;
        }
    }
}
