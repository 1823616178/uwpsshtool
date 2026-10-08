# 同步协议兼容规范（Lumia 端）

> 目标：Lumia 端与桌面端 ssh-tool、鸿蒙端 **共用同一账号、同一保险库、同一份 SyncDocumentV1**，双向同步。
> 本文件是 Lumia 端同步实现的唯一文字依据；与桌面端代码冲突时**以桌面端代码为准**并回改本文件。
>
> 权威来源（只读）：
>
> | 内容 | 文件 |
> |---|---|
> | 文档 schema（严格） | `E:\code\ssh-tool\src\shared\sync-schemas.ts` |
> | 类型与常量 | `E:\code\ssh-tool\src\shared\sync-types.ts`、`sync-private-key.ts`、`types.ts` |
> | 密码学 | `E:\code\ssh-tool\src\main\security\crypto-vault.ts` |
> | API 客户端 | `E:\code\ssh-tool\src\main\sync\api-client.ts` |
> | 序列化 / 合并 / 协调器 | `E:\code\ssh-tool\src\main\sync\sync-serializer.ts`、`sync-merge.ts`、`sync-coordinator.ts` |
> | 保险库缓存 / 认证存储 | `E:\code\ssh-tool\src\main\security\vault-cache.ts`、`auth-store.ts` |
> | 测试场景 | `E:\code\ssh-tool\test\{sync-coordinator,sync-merge,sync-serializer,crypto-vault,api-client}.test.ts` |
> | 服务端接口文档 | `C:\Users\lx182\DevEcoStudioProjects\ssh_client_ohos\docs\api-v1.md` |
> | 鸿蒙端已冻结黄金向量 | `...\ssh_client_ohos\entry\src\main\cpp\tests\vault_golden_vectors.h` |

---

## 1. 范围

| 进同步文档 | 不进（设备本地） |
|---|---|
| 主机 8 个可移植字段（servers[].profile） | 主机的分组归属、密钥引用、外观、跳板、初始命令、环境变量、终端类型、tmux、排序、最近连接 |
| 隧道 12 个字段（tunnels[]） | 隧道 autoStart |
| 分组 id/name/color（groups[]） | 分组 order、折叠状态 |
| 同步开关 syncPasswords/syncPrivateKeys（preferences） | enabled、autoSync（本机偏好，存在 VaultCache） |
| 可选：密码、短语、私钥（servers[].secrets，开关默认关） | 片段、外观、known_hosts、密钥库元数据、设置、日志、token、device.id |

---

## 2. 服务端 API（`{SyncApiBaseUrl}/api/v1/`）

### 2.1 基础约定

- Base URL 规范化：去掉末尾 `/`；若路径不以 `/api/v1` 结尾则追加；不允许带用户信息/查询/片段；`http:` 需要配置显式允许（当前服务器需要）。
- 每个请求头：`Accept: application/json`、`X-Request-Id: <uuid>`；有 body 时 `Content-Type: application/json`。
- 鉴权：除 register/login/refresh 外 `Authorization: Bearer <accessToken>`；本地无 token → 不发请求，直接 `ApiError(kind=authentication, code=AUTH_REQUIRED)`。
- 写操作头：`Idempotency-Key: <uuid v4>`（POST /vault、PUT /sync/document、POST /vault/rotate、POST /sync/revisions/{r}/restore）；
  `If-Match: "revision-<n>"`（PUT /sync/document、POST /vault/rotate、DELETE /sync/revisions、POST restore）。
  revision 必须匹配 `^(0|[1-9]\d*)$`，按字符串处理（u64，C# 用 `ulong.Parse` 比较，不用 double）。
- **Windows.Web.Http 注意**：`If-Match` 带引号值需 `TryAppendWithoutValidation`；`HttpBaseProtocolFilter.CacheControl.ReadBehavior = NoCache`、`WriteBehavior = NoCache`；关闭 Cookie（`CookieUsageBehavior = NoCookies`）；关闭自动重定向。

### 2.2 端点

| 类 | 方法 路径 | 请求体 | 成功响应 |
|---|---|---|---|
| 认证 | POST `auth/register` | `{email,password,inviteCode?,device:{name,platform,appVersion}}` | `{accessToken,refreshToken,expiresIn,user:{id,email},device:{id,name}}` |
| | POST `auth/login` | `{email,password,device}` | 同上（**每次登录新建设备**，配额 10） |
| | POST `auth/refresh` | `{refreshToken}` | 同上（refreshToken 轮换，旧的立即失效） |
| | POST `auth/logout` | — | `{ok:true}` |
| | POST `auth/logout-all` | — | `{revokedDevices,revokedRefreshTokens}` |
| | POST `auth/change-password` | `{currentPassword,newPassword}` | `{ok:true,reauthenticationRequired:true}` |
| 账号 | GET `me` | — | `{user,deviceId}` |
| | DELETE `me` | `{currentPassword,confirmation:"DELETE"}` | `{deleted:true,sessionsInvalidated:true}` |
| 设备 | GET `devices` | — | `{items:[{id,name,platform,appVersion,createdAt,lastSeenAt,current}]}` |
| | PATCH `devices/{id}` | `{name}` | `{device:{id,name}}` |
| | DELETE `devices/{id}` | — | `{ok:true}`（不能撤销当前设备：`DEVICE_CURRENT`） |
| 保险库 | POST `vault` | VaultKeyEnvelope（keyVersion=1） | `{id,keyVersion}` |
| | GET `vault/key-envelope` | — | `{id, ...VaultKeyEnvelope}` |
| | PUT `vault/key-envelope` | `{currentPassword, ...VaultKeyEnvelope}` | `{id,keyVersion}` |
| | POST `vault/rotate` | `{currentPassword,keyEnvelope,document}` | `{id,keyVersion,revision,updatedAt}`（原子清空历史） |
| | DELETE `vault` | `{currentPassword}` | `{deleted:true,vaultId}` |
| 文档 | HEAD `sync/document` | — | 头：`ETag`、`X-Sync-Revision`、`X-Key-Version`、`Last-Modified` |
| | GET `sync/document` | — | `{revision,schemaVersion,keyVersion,algorithm,nonce,ciphertext,ciphertextHash,updatedByDeviceId,updatedAt}` |
| | PUT `sync/document` | EncryptedDocumentEnvelope | `{revision,updatedAt}` |
| 历史 | GET `sync/revisions?limit=20&beforeRevision=` | — | `{items:[{revision,schemaVersion,keyVersion,algorithm,ciphertextHash,createdByDevice:{id,name,platform,appVersion},createdAt}],pagination}` |
| | DELETE `sync/revisions` | — | `{ok,currentRevision,deletedRevisions}` |
| | POST `sync/revisions/{revision}/restore` | — | `{revision,restoredFromRevision,updatedAt}` |

### 2.3 错误模型（与桌面端 `ApiClientError` 同构）

非 2xx 响应体：`{statusCode,statusMessage,message,data:{code,message}}`。客户端只按 `data.code` 分支。

```
ApiError {
  Kind: http | network | timeout | protocol | authentication
  Code: data.code；无则 429→RATE_LIMITED，其他→HTTP_<status>（CodeUnknown=true）
  Status: int?        RequestId: 响应头 x-request-id
  RetryAfterMs: Retry-After 头（秒数或 HTTP 日期）
  Ambiguous: 非 GET/HEAD 请求发生网络错误/超时（服务端可能已执行）
  Message: data.message ?? statusMessage ?? message ?? "服务器请求失败（status）"
}
Kind 判定：status==401 → authentication；其他非 2xx → http；发送异常 → network；超时 → timeout；
           响应 JSON 无效或关键头缺失 → protocol（RESPONSE_INVALID / SYNC_REVISION_INVALID / SYNC_METADATA_INVALID）
```

**需要处理的业务码**：`VALIDATION_ERROR`、`REGISTRATION_DISABLED`、`AUTH_INVITATION_REQUIRED`、`AUTH_INVITATION_INVALID`、`AUTH_EMAIL_EXISTS`、`AUTH_INVALID_CREDENTIALS`、`DEVICE_QUOTA_EXCEEDED`、`AUTH_TOKEN_EXPIRED`、`AUTH_TOKEN_REUSED`、`AUTH_DEVICE_REVOKED`、`AUTH_CURRENT_PASSWORD_INVALID`、`AUTH_PASSWORD_UNCHANGED`、`ACCOUNT_DELETE_CONFLICT`、`DEVICE_NOT_FOUND`、`DEVICE_CURRENT`、`VAULT_EXISTS`、`VAULT_NOT_FOUND`、`VAULT_KEY_VERSION_MISMATCH`、`SYNC_DOCUMENT_NOT_FOUND`、`SYNC_DOCUMENT_INVALID`、`SYNC_DOCUMENT_TOO_LARGE`、`SYNC_REVISION_CONFLICT`、`SYNC_REVISION_LIMIT_REACHED`、`SYNC_REVISION_REQUIRED`、`SYNC_REVISION_NOT_FOUND`、`IDEMPOTENCY_KEY_REQUIRED`、`IDEMPOTENCY_KEY_REUSED`、`RATE_LIMITED`、`MAINTENANCE_MODE`；客户端自产：`AUTH_REQUIRED`、`AUTH_REFRESH_UNAVAILABLE`、`NETWORK_ERROR`、`REQUEST_TIMEOUT`、`RESPONSE_INVALID`、`SYNC_REVISION_INVALID`、`SYNC_METADATA_INVALID`。

### 2.4 发送策略（移植 `sendWithPolicy` / `requestResponse`）

1. **可重试请求** = GET、HEAD、或带 Idempotency-Key 的写请求；尝试次数 = `retryLimit(2) + 1`；其他请求只发 1 次。
2. **可重试错误** = network、timeout、429、≥500。等待 = `RetryAfterMs ?? min(4000, 250 × 2^attempt)` 毫秒。
3. 单次超时 15 s。
4. **401 刷新**：鉴权请求得到 `status==401` 且（`code==AUTH_TOKEN_EXPIRED` 或 `CodeUnknown`）→ `RefreshTokens()`（单飞：并发请求共用同一个刷新任务）→ 原请求再走一遍发送策略；仍失败则抛出。
5. **刷新本身**（`auth/refresh`，只发一次，绝不重试）：
   - AuthStore 无 token → 清空 AuthStore，抛 `AUTH_REFRESH_UNAVAILABLE`；
   - `refreshUncertain==true` **不再**直接清空（fix/persist-login）：仍用该 refreshToken 发一次，由服务端裁决——没被消耗 → 正常轮换；已被消耗 → 401 `AUTH_TOKEN_REUSED`/`AUTH_TOKEN_EXPIRED`，按第 6 条清空（与旧行为同一结局；被撤销的 token family 只属于本设备这次登录）；
   - 发送异常（网络/超时）→ 抛错但**不清会话**；连接阶段失败（`HttpConnectionFailedException`，请求一定没发出）不标 uncertain、`ambiguous=false`，其余标 `MarkRefreshUncertain()`、`ambiguous=true`；
   - 非 2xx → 若为终端鉴权错误则清空 AuthStore，抛出；
   - 成功 → `AuthStore.Save(response)`（`expiresAt = now + expiresIn×1000`，`refreshUncertain=false`）。
6. **终端鉴权错误**（任一请求最终失败时检查）：`AUTH_DEVICE_REVOKED`、`AUTH_TOKEN_REUSED`、或 `status==401 && code==AUTH_TOKEN_EXPIRED` → 清空 AuthStore。
7. **HEAD 无响应体**：HEAD `sync/document` 返回 404 且 `CodeUnknown` → 改发 GET `sync/document` 以拿到准确的 `SYNC_DOCUMENT_NOT_FOUND` / `VAULT_NOT_FOUND`（GET 成功时用其 revision/keyVersion 作为 head）。
   HEAD 成功需校验：`X-Sync-Revision` 合法、`X-Key-Version` 为 ≥1 的安全整数、`ETag` 非空，否则 `SYNC_METADATA_INVALID`。

---

## 3. 密码学

### 3.1 常量（Core `SyncConstants` 与 native `sync_params.h` 同名一致）

| 符号 | 值 |
|---|---|
| `SchemaVersion` | 1 |
| `KdfAlgorithm` | `argon2id` |
| `DefaultKdfMemoryKib` / `Iterations` / `Parallelism` | 65536 / 3 / 1（创建与轮换时使用） |
| KDF 参数允许范围（解锁时校验信封） | memory 8192–1048576；iterations 1–20；parallelism 1–16 |
| `KeyBytes` | 32 |
| `KdfSaltBytes` | 16 |
| `AesAlgorithm` | `AES-256-GCM` |
| `NonceBytes` / `TagBytes` | 12 / 16（tag 拼在密文末尾） |
| `AadDomainDocument` | `ssh-port-mapper/sync-document/v1` |
| `AadDomainPasswordWrap` | `ssh-port-mapper/vault-key/password/v1` |
| `AadDomainRecoveryWrap` | `ssh-port-mapper/vault-key/recovery/v1` |
| `HkdfInfoRecoveryKek` | `ssh-port-mapper/recovery-kek/v1`（HKDF-SHA256，salt 空，输出 32） |
| `RecoveryKeyPrefix` | `SPM1` |
| `DocumentMaxBytes` | 2097152 |
| `PrivateKeyMaxBytes` | 262144 |
| `ServersMax` / `TunnelsMax` / `GroupsMax` | 5000 / 10000 / 1000 |

### 3.2 AAD

`aad(domain, fields) = UTF8( domain + "|" + join("|", fields.map(f => byteLen(f) + ":" + f)) )`，数字字段按十进制字符串。
- 文档：`fields = [vaultId, schemaVersion, keyVersion]`
- 密码包裹：`[keyVersion]`；恢复包裹：`[keyVersion]`

### 3.3 保险库

```
CreateVaultSetup(syncPassword, keyVersion):
  vaultKey = random(32); recoveryKey = GenerateRecoveryKey()
  salt = random(16); pwKey = Argon2id(syncPassword, salt, 65536/3/1, 32)
  recoveryKek = HKDF-SHA256(ikm=DecodeRecoveryKey(recoveryKey), salt=∅, info=HkdfInfoRecoveryKek, 32)
  (n1, c1) = AES-GCM(key=pwKey, pt=vaultKey, aad=aad(PasswordWrap,[keyVersion]))     // n1 随机 12B，c1 = ct||tag
  (n2, c2) = AES-GCM(key=recoveryKek, pt=vaultKey, aad=aad(RecoveryWrap,[keyVersion]))
  envelope = { keyVersion, passwordWrappedKey=b64(c1), passwordWrapNonce=b64(n1),
               recoveryWrappedKey=b64(c2), recoveryWrapNonce=b64(n2), kdfSalt=b64(salt),
               kdfParameters={algorithm:"argon2id", memory:65536, iterations:3, parallelism:1} }
  清零 pwKey、recoveryKek

UnwrapWithPassword(envelope, syncPassword):
  校验 kdfParameters 范围 → pwKey = Argon2id(syncPassword, b64d(kdfSalt), envelope.kdfParameters...)
  vaultKey = AES-GCM-Decrypt(pwKey, b64d(passwordWrapNonce), b64d(passwordWrappedKey), aad(PasswordWrap,[envelope.keyVersion]))

UnwrapWithRecovery(envelope, recoveryKey): 同理用 recoveryKek 与 recovery 字段

GenerateRecoveryKey: raw=random(32) → "SPM1-" + base64url_nopad(raw)(43 字符) + "-" + upper(hex(sha256("SPM1" || raw))[0..12])
DecodeRecoveryKey: 正则 ^SPM1-([A-Za-z0-9_-]{43})-([A-Fa-f0-9]{12})$ → raw 必须 32 字节 → 校验段不区分大小写比较（常量时间）
```
Base64（信封字段）：标准字母表、有填充、**规范形式**（解码再编码必须等于原文），否则视为无效。

### 3.4 文档加解密

```
EncryptSyncDocument(plaintextUtf8, vaultKey, vaultId, schemaVersion=1, keyVersion):
  (nonce, ctTag) = AES-GCM(vaultKey, plaintext, aad(Document,[vaultId, schemaVersion, keyVersion]))
  → { schemaVersion, keyVersion, algorithm:"AES-256-GCM", nonce:b64(nonce), ciphertext:b64(ctTag), ciphertextHash: hexLower(sha256(ctTag)) }

DecryptSyncDocument(envelope, vaultKey, vaultId):
  algorithm 必须 == "AES-256-GCM"
  ctTag = b64d_canonical(ciphertext)；hash = hexLower(sha256(ctTag))；常量时间比较 == ciphertextHash，否则失败
  plaintext = AES-GCM-Decrypt(vaultKey, b64d(nonce), ctTag, aad(Document,[vaultId, envelope.schemaVersion, envelope.keyVersion]))
```

### 3.5 测试向量

1. 复制鸿蒙端 `vault_golden_vectors.h`（已冻结，`kCiphertextHash = c74d2505…4274`）到 `native/tests/`，native 与 C# 两侧断言。
2. `tools/sync-vectors/generate.mjs`：用桌面端相同依赖（`hash-wasm` argon2id、`node:crypto`）按**固定**输入生成：保险库信封（密码与恢复密钥两种解包）、文档密文、恢复密钥编解码，输出 `native/tests/vectors/desktop-vectors.json`；Lumia 端必须能解开，且 Lumia 端用相同 nonce/salt 产出的密文与之逐字节相等。
3. 向量文件一旦提交**禁止修改**。

---

## 4. SyncDocumentV1（严格）

### 4.1 结构与约束（任何多余键、缺失键、类型不符 → 整份无效）

```jsonc
{
  "schemaVersion": 1,                           // 字面量 1
  "updatedAt": "2026-09-17T08:00:00.000Z",      // ISO-8601 datetime（toISOString 形式，UTC，毫秒，Z）
  "preferences": { "syncPasswords": false, "syncPrivateKeys": false },   // 仅这两个 boolean
  "servers": [                                  // ≤ 5000
    {
      "profile": {
        "id": "非空字符串",
        "name": "1–255",
        "host": "1–1024",
        "port": 1,                              // 整数 1–65535
        "username": "1–255",
        "authType": "password|key|agent",
        "hostFingerprint": "≤255（可空串）",
        "keepalive": 30                         // 整数 0–3600
      },
      "secrets": {                              // 可选；出现时下列均为可选键
        "password": "≤4096",
        "passphrase": "≤4096",
        "privateKey": "规范 Base64，解码 ≤ 262144 字节，UTF-8 文本以 -----BEGIN OPENSSH PRIVATE KEY-----\\r?\\n 或 -----BEGIN (RSA |EC |DSA |ENCRYPTED )?PRIVATE KEY-----\\r?\\n 开头",
        "privateKeyEncoding": "base64",         // 字面量
        "privateKeyFormat": "openssh|pem",      // 必须与 header 实际格式一致
        "privateKeyFingerprint": "SHA256:<43 位标准 base64 无填充>"
      }
    }
  ],
  "tunnels": [                                  // ≤ 10000
    {
      "id": "非空", "name": "1–255", "serverId": "非空，必须指向 servers 中存在的 id",
      "groupId": "非空字符串或 null，非 null 时必须指向 groups 中存在的 id",
      "type": "local|remote|dynamic|relay",
      "listenHost": "≤1024", "listenPort": 1,   // 1–65535
      "destHost": "≤1024", "destPort": 0,       // 0–65535
      "destServerId": "≤255；type==relay 时必须指向存在的 server id",
      "autoReconnect": true, "enabled": true
    }
  ],
  "groups": [                                   // ≤ 1000
    { "id": "非空", "name": "1–255", "color": "^#[0-9a-fA-F]{6}$" }
  ]
}
```

**私钥元数据规则**：`privateKey` 缺失时三个元数据键都不得出现；`privateKey` 出现时三个元数据键都必须出现。
**入站私钥额外校验**（桌面端 `decodeSyncedPrivateKey`）：能用同文档 `passphrase`（或无短语）解析时，重新计算的公钥指纹必须等于 `privateKeyFingerprint`；加密私钥且无短语时只做 header/元数据校验。

### 4.2 序列化规则（`SyncDocumentWriter`）

- 键顺序严格按 §4.1 书写顺序；`secrets` 内只写存在的键，顺序 `password, passphrase, privateKey, privateKeyEncoding, privateKeyFormat, privateKeyFingerprint`；`secrets` 为空对象时整个键省略。
- 数组排序：servers 按 `profile.id`、tunnels/groups 按 `id`，**序数比较**（`string.CompareOrdinal`）。
  > 桌面端用 `localeCompare`，对纯 ASCII 的 UUID 结果与序数一致；排序只影响字节形态不影响语义，服务端不比较明文。
- 输出前必须通过 `SyncDocumentValidator`（与 §4.1 一条不差）；UTF-8 字节数 > 2 MiB → 拒绝上传并提示「同步文档过大」。
- `updatedAt` 格式 `yyyy-MM-ddTHH:mm:ss.fffZ`（UTC，InvariantCulture）。

### 4.3 解析规则（`SyncDocumentReader`）

- `JsonTextReader` 逐 token 读取到 `JObject` 后逐键严格校验：未知键 → 失败；缺键 → 失败；类型不符（如 port 为 22.5 或 "22"）→ 失败。
- 失败时抛 `SyncDocumentInvalidException(path, reason)`，协调器进入 `error` 且**绝不上传覆盖**。
- `schemaVersion != 1` → `error`「云端文档版本更高，请升级应用」（只读不写）。

---

## 5. 本地模型 ↔ 文档映射（`SyncLocalAdapter`）

### 5.1 构建本地文档（上行）

| 文档字段 | 来源 |
|---|---|
| `servers[].profile.*` | `Host` 同名 8 字段（name 为空时不允许保存，UI 已校验） |
| `servers[].secrets.password` | `preferences.syncPasswords && authType==password` 且 SecretStore `host:<id>:password` 非空 |
| `servers[].secrets.passphrase` | `syncPasswords` 且（主机私钥短语 SecretStore `key:<keyId>:passphrase` 或 `host:<id>:passphrase`）非空 |
| `servers[].secrets.privateKey…` | `syncPrivateKeys && authType==key && keyId` 有私钥：base64(私钥原文 UTF-8 字节)、format 由 header 判定、fingerprint 由 native `KeyTool.InspectAsync` 计算（S10 前此项不输出） |
| `tunnels[]` | `Tunnel` 除 `autoStart` 外全部字段 |
| `groups[]` | `HostGroup` 的 id/name/color |
| `preferences` | VaultCache.preferences 的 syncPasswords/syncPrivateKeys |
| `updatedAt` | 当前 UTC 时间 |

上行前过滤：serverId 指向不存在主机的隧道、groupId 指向不存在分组的隧道（groupId 置 null）——本地数据若出现这些不一致应在仓库层就阻止，这里是兜底并记警告日志。

### 5.2 应用远端文档（下行）`ApplyDocument(doc)`

1. **主机指纹保护**：本机主机 `hostFingerprint` 非空且远端同 id 主机指纹不同 → 抛错「服务器「name」主机指纹发生变化，需要手动确认」，整份不应用（协调器进 error）。
2. **运行中隧道保护**：对每条运行中的本机隧道，若远端删除它、或其连接相关字段（serverId/type/listenHost/listenPort/destHost/destPort/destServerId）变化、或其主机的 host/port/username/authType 变化 → 抛错「运行中的隧道「name」涉及远端连接变更，请先停止后重试」。
3. **主机**：以远端 servers 为准重建集合；同 id 已存在 → 覆盖 8 个 ☁ 字段，**保留全部 🏠 字段**；新 id → 🏠 字段取默认值；本机有而远端无 → 删除主机（级联删除其凭据与本机 KnownHost 以外的引用：隧道随远端集合处理；`jumpHostId` 指向被删主机的置 null）。
4. **凭据**：远端 `secrets` 中**出现的键**覆盖本机对应凭据；未出现的键保留本机值（「preserves B-only local secrets」）。`privateKey`（S10 起）：校验通过后存为新 KeyEntry（名称 `<host.name> (synced)`，按指纹去重）并设置 `host.keyId`。
5. **隧道**：以远端 tunnels 为准；保留本机 `autoStart`（按 id）；relay 原样保存。
6. **分组**：以远端 groups 为准；保留本机 `order`/`collapsed`；被删除分组的本机主机 `groupId` 置 null。
7. 仓库写入标记 `ChangeOrigin.Sync`（不触发标脏）。

---

## 6. 本地持久状态

### 6.1 AuthState（`secure/auth.bin`）

```
{ version:1, user:{id,email}, device:{id,name}, tokens:{accessToken,refreshToken,expiresAt}, refreshUncertain:false }
```
登录/注册：若已有 AuthState 则不重复登录（避免新建设备）；注销/终端鉴权错误时清空。

### 6.2 VaultCacheState（`secure/vault-cache.bin`）

```
{
  version: 1,
  userId, vaultId, vaultKey (base64 | null，锁定时为 null), keyVersion (0 表示未知),
  revision ("0"), preferences { enabled:false, autoSync:true, syncPasswords:false, syncPrivateKeys:false },
  baseDocument (SyncDocumentV1 | null), dirty:false, lastSyncedAt:null,
  pendingVaultSetup: { idempotencyKey, vaultKey, recoveryKey, keyEnvelope, createdAt } | null,
  pendingUpload:     { idempotencyKey, baseRevision, body(加密信封 JSON 原文), document, createdAt } | null,
  conflict: SyncConflictSummary | null, conflictRemoteDocument | null, conflictRemoteRevision | null
}
```
- 绑定用户：登录用户 id 与 `userId` 不同 → 整个缓存重置（只保留 preferences 默认值）。
- `Lock()`：vaultKey 置 null（其余保留）。`Clear()`：重置为初始（保留 userId）。
- **记住同步密码（feat/remember-vault）**：vaultKey 随本文件（DPAPI `LOCAL=user`）跨重启、跨**同一账号**的退出/重新登录保留，
  只在以下情况清除：换账号（上面的 userId 绑定重置）、删除账号 / 删除保险库（`Clear()`）、设备被吊销（`AUTH_DEVICE_REVOKED`）、
  用户手动锁定、云端 keyVersion / vaultId 与本机不一致（见 §7.1 ProbeVault），以及本机开关「退出登录后记住同步密码」关闭时的退出登录。
  开关存 LocalSettings 键 `Sync.RememberVaultKey`（bool，默认 true，不在 SettingDefinitions 中、不随账号同步）；
  已退出登录时关掉开关立即 `Lock()`，已登录时关掉则在下次退出登录时生效。同步密码本身从不保存，只保存解出的 vaultKey。

### 6.3 SyncState（UI 可观察，不持久化）

`phase`（signed_out/disabled/locked/idle/syncing/synced/offline/error/conflict/auth_error）、`vault`（missing/locked/ready）、`preferences`、`revision`、`keyVersion`、`dirty`、`lastSyncedAt`、`nextRetryAt`、`message`、`conflict`。

---

## 7. 同步算法（移植 `sync-coordinator.ts`）

### 7.1 初始化与认证

```
Initialize():
  session = AuthStore.Load(); cache = BindCacheToSession(session)
  state = { phase: 未登录→signed_out；已登录：enabled ? (vaultKey ? idle : locked) : disabled,
            vault: vaultKey ? ready : (vaultId ? locked : missing), revision/keyVersion/dirty/lastSyncedAt/conflict 取缓存 }
  已登录且无 vaultId → ProbeVaultSafely()
  启动轮询；已登录且 enabled → SyncNow()

Register/Login(input): api.register/login(input, DeviceDescriptor) → AfterAuthenticated()
  DeviceDescriptor = { name: 用户填写或 EasClientDeviceInformation.FriendlyName（≤255）, platform: "windows-mobile-arm" | "windows-uwp-<arch>", appVersion: Package 版本 }
RetirePreviousDevice()（Login/Register 成功后、AfterAuthenticated 前；fix/auth-audit）：
   读本机记住的上一条设备（ILastDeviceStore，LocalSettings 键 Sync.LastDevice，值 userId+deviceId，非机密）→
   先记下新会话的设备 → 同一 userId 且 deviceId 不同时 DELETE devices/{旧 id}；失败（离线 / DEVICE_NOT_FOUND）只记警告，不影响登录、不重试。
   Initialize 读到已登录会话时同样记下当前设备；DeleteAccount / logout-all 成功后清除记录。
AfterAuthenticated(): 绑定缓存（userId 不同 → 整个缓存重置，上一个账号的 vaultKey 不会沿用）→ phase 同上
   → **总是** ProbeVaultSafely()（feat/remember-vault：顺带校验本机记住的 vaultKey 是否仍有效；离线时保留密钥，留给同步时再校验）
ProbeVault(): GET vault/key-envelope →
   成功：cache.vaultId=env.id, keyVersion=env.keyVersion；若 vaultId 变化则 vaultKey=null、revision="0"、baseDocument=null、dirty=false
         vaultId 未变但本机有 vaultKey 且 keyVersion ≠ env.keyVersion（其他设备改了同步密码 / 轮换了密钥）→ vaultKey=null
         上述两种情况若本机原本有 vaultKey，message=RemoteKeyRotated（解锁页顶部说明「同步密码已在其他设备上修改…」，只问一次）
         vault = vaultKey ? ready : locked；phase = vaultKey ? idle : locked
   VAULT_NOT_FOUND：vault=missing, phase=disabled
   其他错误：HandleSyncError（不影响登录成功）
Logout(all): 调 logout 或 logout-all（失败也继续）→ AuthStore.Clear → （仅「记住同步密码」关闭时）cache.Lock → phase=signed_out
   例外（fix/auth-audit）：logout-all 未送达 / 失败（网络、超时、5xx 等非终端鉴权错误）时**保留本机会话并上抛**，
   界面提示失败、可重试——否则界面回到登录页，用户以为其他设备都已下线。会话已失效（终端鉴权错误）时照常清本地。
ChangeAccountPassword(cur,new): api → AuthStore.Clear → （仅「记住同步密码」关闭时）cache.Lock → phase=signed_out（提示用新密码重新登录；
   登录密码与同步密码无关，保险库密钥不受影响）
ChangeSyncPassword / RotateVaultKey：成功后本机直接换存新 vaultKey 与 keyVersion（本机不会再被要求输入）
DeleteAccount(cur): DELETE me {currentPassword, confirmation:"DELETE"} → AuthStore.Clear → cache.Clear → signed_out
```

### 7.2 保险库操作

```
SetupVault(syncPassword):                     // 返回恢复密钥
  pending = cache.pendingVaultSetup
  若无：setup = CreateVaultSetup(syncPassword, 1)；pending = {新 uuid, b64(vaultKey), recoveryKey, envelope, now}
        **先持久化 pending 再发请求**
  created = POST vault(pending.keyEnvelope, pending.idempotencyKey)
  cache: vaultId=created.id, vaultKey=pending.vaultKey, keyVersion=created.keyVersion, revision="0",
         preferences.enabled=true, dirty=true, pendingVaultSetup=null
  state: phase=idle, vault=ready；SyncNow()（忽略错误）；return pending.recoveryKey
  // 响应丢失后重试/重启重试必须复用同一 pending（同材料 + 同幂等键）

UnlockVault(secret, method):
  env = GET vault/key-envelope；key = method==password ? UnwrapWithPassword : UnwrapWithRecovery（失败 → 抛「同步密码不正确/恢复密钥无效」）
  cache: vaultId=env.id, vaultKey=b64(key), keyVersion=env.keyVersion, preferences.enabled=true
  state: phase=idle, vault=ready；SyncNow()
LockVault(): cache.Lock()；state vault=locked, phase=locked
DeleteVault(currentPassword): DELETE vault → cache.Clear → vault=missing, phase=disabled, message「云端保险库和历史版本已删除，本机配置仍保留」

SetPreferences(p):
  若关闭已开启的 syncPasswords 或 syncPrivateKeys → 拒绝（必须走 RotateSensitiveSync）
  cache.preferences=p, dirty=true；phase = enabled ? (vaultKey ? idle : locked) : disabled；enabled && autoSync → MarkDirty()

RotateSensitiveSync(newPrefs, currentPassword, syncPassword):   // 仅用于关闭敏感同步
  前置：已解锁；确有「开→关」
  return RotateVaultKey(newPrefs, currentPassword, syncPassword, "敏感字段已清理，密钥和历史版本已轮换")
ChangeSyncPassword(currentPassword, syncPassword): RotateVaultKey(cache.preferences, ..., "同步密码已更新，请保存新的恢复密钥")

RotateVaultKey(prefs, currentPassword, syncPassword, msg):
  next = cache.keyVersion + 1；setup = CreateVaultSetup(syncPassword, next)；prev = cache.preferences
  try:
    cache.preferences = prefs；doc = BuildLocalDocument()
    enc = EncryptSyncDocument(doc, setup.vaultKey, vaultId, 1, next)
    resp = POST vault/rotate { currentPassword, keyEnvelope: setup.envelope, document: enc }  (If-Match=cache.revision, 新幂等键)
    resp.keyVersion != next → 抛「服务端返回了意外的密钥版本」
    LocalAdapter.Apply(doc)
    cache: vaultKey=b64(setup.vaultKey), keyVersion=resp.keyVersion, revision=resp.revision, preferences=prefs,
           baseDocument=doc, dirty=false, pendingUpload=null, conflict*=null, lastSyncedAt=resp.updatedAt
    state: phase=synced, message=msg；return setup.recoveryKey
  catch e:
    cache.preferences = prev
    e.Ambiguous → phase=error, message「轮换结果未知，若云端已经轮换，请用新的同步密码重新解锁保险库」
    rethrow
```

### 7.3 PerformSync(strategy?)  —— strategy ∈ { null, keep-local, use-remote }

```
SyncNow(input): 若已有运行中的同步 → 返回同一个 Task（单飞）
PerformSync:
  未登录 → signed_out；!enabled → disabled；无 vaultKey/vaultId → locked（vault = vaultId ? locked : missing）
  state: syncing, message="", nextRetryAt=null
  try
    local = BuildLocalDocument()
    // ① 重放未确认的上传
    if cache.pendingUpload:
       r = PUT sync/document(body 原文, If-Match=pending.baseRevision, 同一幂等键)
       stillCurrent = SameContent(local, pending.document)       // 忽略 updatedAt 比较
       cache: revision=r.revision, baseDocument=pending.document, dirty=!stillCurrent, pendingUpload=null, lastSyncedAt=r.updatedAt
       stillCurrent → synced, return；否则 local = BuildLocalDocument()
    // ② 探测
    try head = HEAD sync/document
    catch SYNC_DOCUMENT_NOT_FOUND → head = { revision:"0", keyVersion: cache.keyVersion }
    catch VAULT_NOT_FOUND → cache.Lock；vault=missing, phase=disabled, message「云端保险库已被删除，请重新创建同步保险库」；return
    if head.keyVersion != cache.keyVersion → cache.Lock；phase=locked, vault=locked, message「云端密钥已轮换，请重新输入同步密码或恢复密钥」；return
    if head.revision < cache.revision → 抛「云端 revision 低于本机基线，已停止上传以避免覆盖」
    // ③ 云端没有新版本
    if head.revision == cache.revision:
       if cache.dirty || head.revision=="0" || strategy==keep-local → Upload(local, head.revision)
       else → synced
       return
    // ④ 云端有新版本
    resp = GET sync/document；remote = Decode(Decrypt(resp))
    if cache.baseDocument == null && strategy == null → SaveConflict(local, remote, resp.revision,
          [{settings, "initial-import", "*", false}], initial-import)；return
    if !cache.dirty && strategy == null && cache.baseDocument != null:
       deletions = RemoteDeletionConflicts(base, remote)        // base 中有、remote 中没有的 server/tunnel/group
       if deletions 非空 → SaveConflict(..., deletions, remote-deletion)；return
    if !cache.dirty || strategy == use-remote → LocalAdapter.Apply(remote)；CommitRemote(remote, resp.revision)；return
    if cache.baseDocument == null → SaveConflict(..., [{settings,"initial","*",false}], initial-import)；return
    merged = Merge(base, local, remote)
    if merged.conflicts 非空 → SaveConflict(local, remote, resp.revision, merged.conflicts, merge-conflict)；return
    LocalAdapter.Apply(merged.document)；cache.revision=resp.revision, dirty=true
    Upload(merged.document, resp.revision)
  catch e → HandleSyncError(e)；rethrow（调用方可忽略）

Upload(doc, baseRevision):
  generation = changeGeneration
  enc = Encrypt(Encode(doc), vaultKey, vaultId, 1, keyVersion)；key = 新 uuid
  **先持久化** cache.pendingUpload = { key, baseRevision, body: JSON(enc), document: doc, now }
  try r = PUT sync/document(enc, If-Match=baseRevision, key)
  catch e: e.code ∈ {SYNC_REVISION_CONFLICT, VAULT_KEY_VERSION_MISMATCH, IDEMPOTENCY_KEY_REUSED} → pendingUpload=null；rethrow
  changed = generation != changeGeneration
  cache: revision=r.revision, baseDocument=doc, dirty=changed, pendingUpload=null, lastSyncedAt=now, conflict=null
  retryAttempt=0；phase = changed ? idle : synced
  changed && autoSync → 250 ms 后 SyncNow()

CommitRemote(doc, revision): cache: baseDocument=doc, revision, dirty=false, lastSyncedAt=now, conflict*=null；phase=synced

SaveConflict(local, remote, remoteRevision, fields, reason):
  summary = { reason, localRevision: cache.revision, remoteRevision, localUpdatedAt, remoteUpdatedAt,
              remoteSummary:{servers,tunnels,groups 数量, includesPasswords, includesPrivateKeys}, fields }
  cache: conflict=summary, conflictRemoteDocument=remote, conflictRemoteRevision
  phase=conflict；message 按 reason：「已解锁云端配置，请选择首次同步方式」/「云端包含删除操作，请确认后再应用」/「检测到需要确认的同步冲突」

ResolveConflict(strategy):
  无待处理冲突 → 抛
  use-remote：LocalAdapter.Apply(remote)；CommitRemote(remote, revision)
  keep-local：cache: revision=conflictRemoteRevision, dirty=true, conflict*=null；phase=idle；SyncNow(keep-local)

RestoreRevision(r): POST sync/revisions/{r}/restore (If-Match=cache.revision, 新幂等键) → SyncNow(use-remote)
ClearRevisions(): DELETE sync/revisions (If-Match=cache.revision)
MarkDirty(): !enabled → return；changeGeneration++；cache.dirty=true；autoSync → 防抖 3000 ms 后 SyncNow()
```
> 409 `SYNC_REVISION_CONFLICT` 不在 Upload 内重试：错误抛出后下一次同步（自动重试或用户点击）走 ④ 拉远端合并——这正是桌面端测试「surfaces an optimistic 409, then detects the three-way conflict on retry」的行为。

### 7.4 HandleSyncError

```
终端鉴权（kind==authentication 且 code ∈ {AUTH_DEVICE_REVOKED, AUTH_TOKEN_REUSED, AUTH_REFRESH_UNAVAILABLE} 或 status==401 且带服务端 data.code（无码 401 不算，fix/persist-login））：
   AuthStore.Clear；cache.Lock（仅 AUTH_DEVICE_REVOKED 或「记住同步密码」关闭时；会话过期 / refresh 失效只需重新登录，
   同一账号不再要求同步密码）；phase=auth_error，vault = vaultId ? locked : missing，message，nextRetryAt=null
AuthStore 已无会话：（「记住同步密码」关闭时）cache.Lock；phase=signed_out
其他：
   offline = kind ∈ {network, timeout}
   retryable = offline || status==429 || status>=500
   retryable && autoSync：steps=[1,2,5,10,30,60,300] 秒，delay = RetryAfterMs ?? steps[min(retryAttempt,6)]×1000；retryAttempt++；
                          nextRetryAt=now+delay；定时 SyncNow()
   phase = offline ? offline : error；message = e.Message + (RequestId ? "（请求 ID：xxx）" : "")
```

### 7.5 触发器

| 触发 | 条件 | 动作 |
|---|---|---|
| 应用启动 | 已登录且 enabled | SyncNow |
| 保存配置（仓库 Changed，origin=User，涉及主机/隧道/分组/主机凭据） | enabled | MarkDirty（防抖 3 s） |
| 回到前台（Resuming / 窗口可见） | 已登录 enabled autoSync，距上次同步 > 30 s | SyncNow |
| 网络恢复（`NetworkStatusChanged` 且有 Internet） | 同上 | SyncNow |
| 轮询 | 前台，间隔 `syncPollForegroundSeconds`（默认 60，0 关）；后台不轮询 | SyncNow |
| 手动 | 用户点击 | SyncNow（显示结果） |

---

## 8. 三方合并（移植 `sync-merge.ts`）

```
Merge(base, local, remote)：三者 schemaVersion 都必须为 1
  servers  = MergeEntityArray(idOf = profile.id, mergeExisting = MergeServer)
  tunnels  = MergeEntityArray(idOf = id, mergeExisting = MergeFields(entity=tunnel, prefix=""))
  groups   = MergeEntityArray(idOf = id, mergeExisting = MergeFields(entity=group, prefix=""))
  preferences = MergeFields(base.p, local.p, remote.p, entity=settings, id="preferences", prefix="preferences", 全部敏感)
  updatedAt = max(local.updatedAt, remote.updatedAt)

MergeValue(b, l, r, conflictInfo)：
  !Changed(l,b) → r；   !Changed(r,b) 或 DeepEqual(l,r) → l；   否则记 field 冲突，暂取 l

MergeFields(b, l, r, entity, id, prefix, isSensitive)：对三者键并集逐键 MergeValue，field = prefix ? prefix+"."+key : key

MergeServer(b, l, r)：
  profile = MergeFields(b.profile, l.profile, r.profile, server, id, "profile", 敏感 = field=="profile.hostFingerprint")
  secrets = MergeFields(b.secrets??{}, l.secrets??{}, r.secrets??{}, server, id, "secrets", 全部敏感)
  secrets 任一值非空 → 带上；否则省略

MergeEntityArray(ids = base∪local∪remote 排序)：
  base 无：local&remote 都有 → 不 DeepEqual 记 add-add（field "*"，sensitive = entity==server），取 local；只有一边 → 取那一边
  base 有：两边都没了 → 删除
          local 没了：remote 与 base 相同 → 删除；否则记 delete-modify，**不保留**（remote 修改被本地删除覆盖待用户决定）
          remote 没了：local 与 base 相同 → 删除；否则记 delete-modify，保留 local
          都在 → mergeExisting
```
> DeepEqual 按 JSON 语义（键集合相同、值递归相等，`undefined` 键视为不存在）。C# 用 `JToken.DeepEquals` 前先剔除 null 值的可选键。

---

## 9. 私钥同步（S10）

- 出站：私钥原文字节（UTF-8 文本）≤ 256 KiB；format 由 header 判定；`privateKeyFingerprint = "SHA256:" + base64_nopad(sha256(公钥 SSH wire blob))`；解析需要短语时使用本机已保存短语，拿不到短语无法解析 → 该主机不输出 privateKey 并在同步状态中给出警告。
- 公钥 wire blob：ed25519 = `string "ssh-ed25519" + string pk32`；rsa = `string "ssh-rsa" + mpint e + mpint n`；ecdsa = `string "ecdsa-sha2-nistpXXX" + string "nistpXXX" + string Q`。
  openssh-key-v1 格式可直接取文件头中的公钥 blob（无需短语）；PEM 需用 OpenSSL 解析（加密 PEM 需短语）。
- 入站：§4.1 规则 + 指纹复算（能解析时），失败则整份文档视为无效（与桌面端一致，抛错进入 error）。
- 验证：`tools/sync-vectors` 用桌面端 `ssh2.utils.parseKey(...).getPublicSSH()` 为 ed25519/rsa/ecdsa（openssh 与 pem、加密与不加密）生成期望指纹。

---

## 10. 测试矩阵

### 10.1 Core 单测（必须全部实现，名称对齐桌面端用例）

**ApiClient**（假 `IHttpTransport`）
1. 允许显式开启的 HTTP，拒绝未开启的 HTTP 与非 http(s) 协议
2. 使用 `/api/v1` 前缀并发送 If-Match 与 Idempotency-Key
3. 刷新一次、保存轮换后的 token、重放受保护请求；并发两个 401 只刷新一次
4. HEAD `sync/document` 无响应体 404 → 通过 GET 得到 `SYNC_DOCUMENT_NOT_FOUND`
5. 同上得到 `VAULT_NOT_FOUND`
6. HEAD 无响应体 401 → 刷新而不是丢会话
7. 刷新 POST 网络失败 → 当次不重试、标记 refreshUncertain、不清会话；下次 401 仍用同一 refreshToken 试一次（fix/persist-login）
8. 遵守 Retry-After 后重试安全请求；非幂等 POST 不重试
9. 错误码与 request id 保持稳定

**Serializer / Validator**
1. 严格文档往返（读 → 写 → 读 等价，写出字节确定）
2. 拒绝多余键 / 缺键 / 类型错误 / 越界（逐条覆盖 §4.1 每个约束）
3. 拒绝隧道引用不存在的主机、分组、relay 目标
4. 私钥 > 256 KiB 拒绝；元数据不全拒绝；伪造 header 拒绝；指纹格式非法拒绝
5. 用桌面端真实导出的文档夹具（`tests/fixtures/desktop-sync-document-*.json`，由 tools 脚本生成的脱敏样例）解析成功且重新序列化后桌面端 zod 能通过（tools 脚本反向校验）

**Merge**
1. 不同字段的修改自动合并
2. 同字段冲突上报并暂取本地值
3. 指纹与凭据冲突标记为敏感且冲突信息不含值
4. delete-vs-modify 冲突上报（两个方向）
5. add-add 相同内容不冲突、不同内容冲突

**Coordinator**（假 Api + 内存 VaultCache + 假 LocalAdapter + 假 Crypto）
1. 新设备发现已有保险库并解锁
2. 新账号无云端保险库时保持 missing
3. 保险库探测离线不影响登录/初始化
4. 创建保险库并完成首次加密上传
5. 响应丢失、重启、用户重试后复用同一份保险库创建材料与幂等键
6. A 设备修改后 B 设备下载，并保留 B 独有的本机凭据与本机专有字段
7. 干净设备应用远端删除前要求确认（remote-deletion）
8. 网络失败进入 offline 并安排自动重试（nextRetryAt 符合退避表）
9. 响应丢失后用完全相同的 body 与幂等键续传 pendingUpload
10. 关闭敏感同步时轮换密钥，旧密钥不可用
11. 轮换失败时回滚 preferences
12. 修改同步密码使旧同步密码与旧恢复密钥失效
13. 保险库锁定时拒绝修改同步密码
14. 乐观锁 409 先报错，重试时检测到三方冲突
15. 冲突选择 use-remote 后保留本机凭据
16. 冲突选择 keep-local 后以远端 revision 为基准上传覆盖
17. 远端主机指纹变化时拒绝替换本机配置
18. 运行中隧道涉及连接变更时拒绝替换
19. 同步文档与日志不暴露密码、短语、私钥路径（未开启开关时）
20. keyVersion 不一致 → locked；head.revision 小于本机 → error 且不上传
21. 首次同步本机有数据、云端有数据 → initial-import 冲突；选择 use-remote/keep-local 两条路径

### 10.2 native / 跨端向量

- 鸿蒙端黄金向量全部通过；桌面端生成向量双向互解；恢复密钥 10000 次随机往返 + 任一字符篡改被拒。

### 10.3 手工互通验收（S11，📱）

| # | 场景 | 期望 |
|---|---|---|
| 1 | 桌面端已有账号与保险库，Lumia 登录 → 用同步密码解锁 | 出现 initial-import（Lumia 本机为空时直接应用）；主机/隧道/分组与桌面端一致 |
| 2 | Lumia 新增主机、修改端口 → 桌面端同步 | 桌面端看到变更，桌面端不报 schema 错误 |
| 3 | 桌面端修改隧道名称与分组颜色 → Lumia 同步 | Lumia 显示新值；Lumia 本机分组排序、主机分组归属、tmux 设置不变 |
| 4 | Lumia 断网修改 → 联网 | 自动补传；桌面端看到 |
| 5 | 两端同时改同一主机端口 | 后同步的一端出现冲突页；两种选择都正确 |
| 6 | 桌面端删除主机 → Lumia（无本地修改） | 弹 remote-deletion 确认；确认后删除 |
| 7 | 桌面端开启同步密码 → Lumia 开启同步密码 → 同步 | Lumia 可免输密码连接 |
| 8 | Lumia 关闭同步密码 → 安全清理 | 获得新恢复密钥；桌面端提示云端密钥已轮换需重新解锁；历史清空 |
| 9 | 鸿蒙端参与 1–6 | 三端一致 |
| 10 | Lumia 恢复历史版本 | 本机与桌面端均回到该版本内容 |

---

## 11. 踩坑清单（实现时逐条自查）

1. **KDF 参数必须读信封**，不能写死（鸿蒙端 native 写死了常量，桌面端改参数后会解不开）。
2. `ciphertextHash` 必须在解密**前**校验；Base64 必须规范形式。
3. `username` 与 `name` 必须非空（桌面端 schema `min(1)`）；本机允许空名称时上行会被桌面端整份拒绝 → UI 强制必填。
4. `groups[].color` 必填正则；本机新建分组必须给颜色。
5. 隧道 `groupId` 引用的是 groups；删除分组前处理引用。
6. `relay` 隧道必须能编辑与保存，`destServerId` 必须指向存在的主机。
7. `updatedAt` 必须是 ISO datetime（C# `DateTime.ToString("o")` 输出 7 位小数 + `Z` 可能被 zod `z.iso.datetime()` 拒绝 → 固定 `yyyy-MM-ddTHH:mm:ss.fffZ`）。
8. revision 是 u64 字符串，不要转 double/int 比较。
9. `Windows.Web.Http` 默认缓存 GET、会自动带 Cookie —— 必须关闭。
10. login 每次新建设备：已登录状态下不要再 login；登录前检查 AuthState。logout 不撤销设备记录，退出后重登会留下死记录，攒满 10 台后本机再也登录不上（DEVICE_QUOTA_EXCEEDED）——登录成功后撤销本机上一条记录（见 §7.1 RetirePreviousDevice）。
11. 刷新请求网络失败后 refresh token 可能已被消耗：**同一次刷新内不得重试**。fix/persist-login 起，下一次 401 时仍用它再试一次——若确已消耗，服务端回 `AUTH_TOKEN_REUSED` 吊销本设备这串 token family，客户端清空会话（与「直接清空」同一结局）；若其实没送达（Lumia 上更常见），就不会因一次网络抖动被强制登出。
12. pendingUpload / pendingVaultSetup **先落盘再发请求**。
13. 下行应用时来源标记为 Sync，不得再触发 MarkDirty（否则无限上传循环）。
14. 同步密码 ≠ 登录密码；轮换接口的 `currentPassword` 是**账号登录密码**。
15. 数组排序用序数比较；JSON 写出不要带 BOM，不要缩进。
16. 日志只记相位、数量、revision，不记文档内容与任何密钥材料。
