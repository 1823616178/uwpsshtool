# Q03 安全自查报告（doc/SECURITY-REPORT.md）

> 依据 `01-DESIGN.md §12`、`03-SYNC-PROTOCOL.md §11`、04-TASKS Q03 要点逐项自查。
> 每项给出：**结论**（当前状态）+ **人工操作指南**（需要真机/外部环境执行的部分，👤）。
> 自查日期：2026-09-21（代码层与 CVE 部分）；👤 项待人工执行后在本文件回填结果。

## 1. LocalFolder 敏感数据搜索

**检查目的**：设备门户下载 LocalFolder，确认搜索不到测试密码、私钥片段、恢复密钥。

**代码层证据与结论**：

- 凭据唯一落盘点是 `LocalFolder/secure/secrets.bin`，由 `DpapiSecureFile`
  以 `DataProtectionProvider("LOCAL=user")` 整段加密后写入（`src/SshTool.App/Platform/DpapiSecureFile.cs`，
  D03/D10）；`DpapiSecretStore` 是 `ISecretStore` 的唯一真实现。明文只在
  Protect/Unprotect 的内存瞬间存在。
- 私钥材料经 native 处理：`KeyTool`/`SshAgent`/`VaultCrypto`（C++/CX）内部清零
  （`OPENSSL_cleanse`），C# 侧不落盘（`NativeSshAgent`/`NativeKeyTool`/`NativeVaultCrypto`
  头注释为契约）。恢复密钥只在 `RecoveryKeyDialog` 内存与剪贴板，不持久化。
- 同步保险库缓存 `VaultCache`（S02/S11）只存密文与 vaultKey 派生材料，密文不可解
  （密钥在 DPAPI 保护的 secrets.bin 内）。

**👤 操作指南**（待执行，结果回填）：

1. 设备门户（见 doc/ENV.md §3.1）→ File explorer → LocalState 全量下载。
2. 在下载目录搜以下模式（区分大小写组合都试）：
   - 测试密码原文（U15 测试账号密码、DebugSshDefaults 密码）；
   - `-----BEGIN OPENSSH PRIVATE KEY-----`、`-----BEGIN PRIVATE KEY-----`；
   - `SPM1-`（恢复密钥前缀）、恢复密钥正文段；
   - `host:` + 已删除主机 id（验证 §4）。
3. 记录：`secrets.bin` 大小与修改时间（应为密文二进制）、命中列表（预期：零命中）。

**状态**：⏳ 👤 待人工执行。

## 2. 日志抽查

**检查目的**：app.log 及各调试报告不含密码/私钥/token/恢复密钥/同步明文密文。

**代码层证据与结论**：

- `LogRedactor`（`src/SshTool.Core/Common/LogRedactor.cs`）按 §12.2 剥离：
  键名（大小写不敏感）`password|passphrase|token|accessToken|refreshToken|privateKey|recoveryKey|ciphertext|authorization|syncPassword`
  的 `key=value` 与 JSON `"key":"value"` 形式整值替换 `***`；PEM 块、`SPM1-` 恢复密钥、
  `Bearer` 令牌同。
- `FileLogger` 写盘前**必须**过 `Redact`（X04）；`AppLog` 只记异常类型名。
- 单测：`tests/SshTool.Core.Tests/Common/LogRedactorTests.cs` 全绿（verify 步骤①内）。
- 上游纪律：各 native 桥与同步组件（`SyncCoordinator`/`AuthService`/`KeyImportService`/
  `NativeSshAgent` 等）按「只记相位与计数」实现并在头注释声明；同步日志不含文档
  内容与密钥材料（`03-SYNC-PROTOCOL.md §11.16`）。

**👤 操作指南**：取回 `LocalState/logs/app.log` 后：
搜 `password=`、`"password"`、`token`、`SPM1-`、`BEGIN .*PRIVATE`、`Authorization`、
测试密码原文——预期所有命中均已是 `***` 形式；抽查同步时段日志只含 revision/计数。

**状态**：⏳ 👤 待人工执行（代码层审计已通过）。

## 3. 抓包确认文档为密文 + 登录明文风险

**检查目的**：Wireshark 抓包确认同步文档是密文；记录登录通道明文风险。

**代码层证据与结论**：

- 文档内容端到端加密（SyncDocumentV1，AES-GCM + Argon2id 派生，S01–S05 已向量化
  对拍桌面端），抓包可见的请求体应为 `ciphertext`+`iv`+`tag` 字段。
- **已知风险（R12/§12.3）**：服务端为公网裸 IP + HTTP，登录/注册/刷新请求中的
  邮箱、登录密码、access/refresh token、保险库信封为**明文可截获**。缓解：
  - 登录/注册页固定明文 HTTP 风险 Banner（U15，`LoginPage.xaml`）；
  - 不提供「记住密码」自动登录（token 由 refresh 维持）；
  - `AppConfig.SyncApiBaseUrl` + `AllowHttp` 为配置项，服务端挂 TLS 后切 https 不改代码
    （`AppConfig.cs`/`AppServices.cs`）。
- `Windows.Web.Http` 默认缓存与 Cookie 已显式关闭（§11.9）。

**👤 操作指南**（电脑热点 + Wireshark）：

1. 手机连电脑热点；Wireshark 抓热点网卡，过滤 `ip.addr == <手机IP>`。
2. 触发一次同步（编辑一台主机名）；找 `POST /vault/...`：确认 body 为 base64 密文块。
3. 登录一次：确认 `/auth/login` body 明文可见 → 截图记入本节（这就是 §12.3 已登记的
   风险证据，切 https 后复测应消失）。

**状态**：⏳ 👤 待人工执行（风险已在 R12 登记，缓解措施已实现）。

## 4. 删除主机后凭据消失

**检查目的**：删除主机后，其保存在 SecretStore 的密码/短语立即不可用且文件中消失。

**代码层证据与结论**：

- `ConfigService.DeleteHostAsync`（`src/SshTool.Core/Storage/ConfigService.cs`）：
  删隧道（含 relay `destServerId` 指向者）→ 清其他主机 `jumpHostId` → 删主机 →
  `RemoveByPrefixAsync(SecretKeys.HostPrefix(hostId))` 级联删凭据；
  `DeleteKeyAsync` 同样按 `key:` 前缀级联且密钥被引用时拒绝删除。
- 下行同步「远端已删主机」复用同一级联清凭据（`SyncLocalAdapter.cs`「缺失即保留、
  凭据按 host: 前缀清」）。
- 单测：`tests/SshTool.Core.Tests/Storage/ConfigServiceTests.cs`（级联删凭据用例全绿）。

**👤 操作指南**：真机删除一台已保存密码的主机 → 设备门户下载 `secure/secrets.bin`
→ 按 §1 方法搜索该主机 id 与密码（预期无命中；由于是整段加密文件，等效验证是
重建同名 id 主机后不再自动填充——id 为新生成，实际以上一步为准）。

**状态**：⏳ 👤 待人工执行（逻辑有单测覆盖）。

## 5. WACK（Windows 应用认证工具）

**检查目的**：Windows App Certification Kit 对发布包跑认证，确认无崩溃/性能/合规问题。

**👤 操作指南**：

1. 产出 Release/ARM 包（`verify.ps1 -Arm`，含 `-p:EnableDebugPages=false` 的正式
   配置另出，见 Q09）。
2. Windows SDK 的 `appcert.exe test -appx <包路径> -platform ARM`（或在 Windows
   App Certification Kit GUI 选验证现有 App）。
3. 结果 XML 存 `doc/` 旁路不入库，结论回填本节：崩溃测试、字节码合规
   （.NET Native 下主要看 API 使用合规）、性能启动测试。

**状态**：⏳ 👤 待人工执行。

## 6. OpenSSL / libssh2 版本 CVE 检查（已完成）

### 6.1 libssh2 1.11.1 —— **发现高危 CVE，已登记任务 Q11**

- **CVE-2026-55200**（CVSS 9.2）：`ssh2_transport_read()` 对 `packet_length` 无上限
  检查 → 32 位整数溢出 → 堆越界写；**认证前可由恶意 SSH 服务器触发**，可致 RCE，
  **PoC 已公开**。上游修复 commit `7acf3df`。
- **CVE-2026-66033**：`openssl.c ssh2_cipher_crypt` 整数下溢（AES-GCM 路径），
  **认证前可触发**，内存破坏/DoS。修复 commit `a2ed82d`。
- **CVE-2026-66032**：`sftp.c sftp_open()` 双重释放（认证后）。
- 另有 2026-07-24 批量披露的配套漏洞（合计 4 个高危，影响 ≤1.11.1）。
- **上游至今无修复版发布**（最新 release 仍为 1.11.1），修复只在上游 main 分支 commits。
- **与本应用的关系**：SSH 客户端会连接用户配置的任意主机——被劫持/恶意服务器在
  认证前即可攻击客户端，攻击面完全匹配，**风险：高**。
- **处置**：登记 **Q11（M8 新任务）：cherry-pick 上游修复 commits 到
  `native/third_party/libssh2` 并在 `PATCHES.md` 登记（机制已有，当前记录为「零补丁」），
  重建三/四个 triplet 后全量回归。** 在 Q11 完成前，发布包须附带该说明。

### 6.2 OpenSSL 3.6.3 —— 建议随 Q11 升级到 3.6.4（低风险）

- 3.6.3（vcpkg ref `2026.07.29`，2026-09-17 构建）本身是 2026-06 的安全补丁版。
- 2026-08-25 官方公告：**3.6.4** 修复 11 个 CVE（最高 Moderate：CMS 解密越界写、
  QUIC 双重释放等）。
- 攻击面评估：本应用只用 libcrypto 的 EVP 原语（AES-GCM/SHA2/Ed25519，SSH 与保险库），
  Argon2id 为独立 reference 实现；**不使用 CMS/QUIC**；ChaCha20-Poly1305 的
  「空密文解密」问题不适用于 SSH 完整分组路径。**实际风险：低**。
- 处置：升级条目并入 Q11（`scripts/build-openssl.ps1` 换 vcpkg ref 重跑即可）。

## 7. 发现的问题与处置汇总

| # | 问题 | 严重度 | 处置 |
|---|---|---|---|
| 1 | libssh2 1.11.1 四个高危 CVE（含认证前 RCE，PoC 公开） | 高 | 已登记 **Q11**（升级/打补丁 + 回归） |
| 2 | OpenSSL 3.6.3 → 3.6.4（11 个 Moderate CVE） | 低 | 并入 Q11 一并升级 |
| 3 | 登录通道明文 HTTP（邮箱/密码/token） | 已知风险（R12） | 缓解已实现（§3）；服务端 TLS 后切 `SyncApiBaseUrl` |

## 8. 变更记录

- 2026-09-21：Q03 代码层审计与 CVE 检查完成；§1–§5 人工操作指南就绪，待 👤 执行回填；
  发现 libssh2 高危 CVE → 登记任务 Q11。
