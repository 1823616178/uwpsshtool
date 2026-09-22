# Lumia SSH v1.0.0 发布前总检清单（doc/RELEASE-CHECKLIST.md）

> 依据 `04-TASKS.md` **Q10 v1.0.0 发布前总检** 要求编制。
> 本清单作为 v1.0.0 正式发布前对代码实现、架构设计、安全合规、跨端互通与真机待办的最终综合验收。

---

## 1. 发布版本与基本元数据

| 项目 | 配置值 / 状态 | 验证依据 |
|---|---|---|
| **应用名称** | Lumia SSH | `Package.appxmanifest` / `Resources.resw` |
| **程序集版本** | `1.0.0.0` | `src/SshTool.App/Properties/AssemblyInfo.cs` |
| **包版本号** | `1.0.0.0` | `src/SshTool.App/Package.appxmanifest` Identity Version |
| **包标识 (Identity)** | `Name="SshTool.LumiaSsh"` | `Package.appxmanifest` |
| **发布者 (Publisher)** | `CN=LumiaSshDev` | 自签名证书 / `Package.appxmanifest` |
| **支持平台** | Windows 10 Mobile / Windows 10 UWP | Target 19041, Min 15063 |
| **目标架构** | ARM32（正式发布）、x64（开发/测试） | Release ARM / x64 Debug |
| **编译工具链** | Native: VS2017 (v141) / sln: VS2026 (.NET Native) | `doc/ENV.md §5` 两段式构建 |
| **发布 Git Tag** | `v1.0.0` | 本次发布标记 |

---

## 2. 里程碑实现情况与设计一致性复核

全工程共完成 **10 个里程碑、127 个任务项**，全部代码与自动化测试均已闭环：

| 里程碑 | 名称 | 任务总数 | 完成数 | 核心交付物与一致性核对结论 |
|---|---|---|---|---|
| **M0** | 基座与技术验证 | 13 | 13 | Lumia ARM32 基座跑通，6 项核心 Spike 技术验证结论全部落档入库。 |
| **M1** | 原生 SSH 内核 | 11 | 11 | 基于 libssh2 + OpenSSL 原生 C++ 异步内核，非阻塞 I/O、状态机、错误码归一化。 |
| **M2** | 终端引擎与渲染 | 15 | 15 | libvterm 网格、Win2D 脏行重绘、软键盘哨兵差分输入、KeyBar 42 键触控键条。 |
| **M3** | 数据层与主机管理 | 12 | 12 | DPAPI 本地凭据整段加密存储（secrets.bin）、主机/分组增删改查、连接测试。 |
| **M4** | 终端页与会话 | 12 | 12 | 完整可用的本地 SSH 客户端，多会话管理、自动断网重连、tmux 现场恢复。 |
| **M5** | 云端同步 | 22 | 22 | 端到端加密（Argon2id + AES-GCM）、双向增量同步、三向冲突合并、密码安全轮换。 |
| **M6** | 外观系统 | 5 | 5 | 8 套内置配色方案、自定义外观创建、iTerm2 / Windows Terminal 主题导入。 |
| **M7** | 密钥、SFTP、转发、跳板 | 11 | 11 | Ed25519/RSA 密钥生成导入、SFTP 文件传输、本地/远程/动态转发、多跳跳板连接。 |
| **M8** | 打磨与发布 | 11 | 11 | 性能报告、安全自查报告、无障碍支持、图标磁贴、Continuum 适配、侧载指南、总检。 |
| **M9** | 优化与债务清理 | 15 | 15 | 内存泄漏治理、热路径无锁提速、全可见文本资源化（硬编码清零）、生命周期防守。 |
| **合计** | | **127** | **127** | **100% 任务完成，全流水线 8 步验证全绿。** |

---

## 3. 安全合规与漏洞闭环（Q03 / Q11）

1. **高危 CVE 治理（Q11）**：
   - 针对 libssh2 1.11.1 在 2026 年披露的 5 处严重安全漏洞（CVE-2026-55200 CVSS 9.2 RCE、CVE-2026-66032、CVE-2026-66033、CVE-2026-66034、CVE-2026-66035），成功 backport 上游修复补丁至 `native/patches/libssh2/`；
   - 自动化集成入 `scripts/fetch-third-party.ps1`，`deps_smoke_test.cpp` 增加边界常量断言，所有回归测试全部通过。
2. **本地敏感数据保护（Q03 §1）**：
   - 密码与私钥使用 Windows DPAPI `LOCAL=user` 保护写入 `secure/secrets.bin`；
   - Native 内存敏感数据在使用后严格经 `OPENSSL_cleanse` 清零。
3. **日志脱敏（Q03 §2）**：
   - `LogRedactor` 实施全局敏感字段正则过滤，保证 `app.log` 零泄漏密码、Token 与密钥材料。
4. **端到端加密与传输安全（Q03 §3）**：
   - 保险库文档在本地由 AES-GCM + Argon2id 派生密钥加密后上传，服务端无法解密；
   - 登录界面明确标注 HTTP 明文传输风险 Banner，缓解公网网络截获风险。

---

## 4. 跨端互通性确认（S04 / S15 / S16）

- **互通黄金向量**：桌面端生成的 `sync_doc_v1.golden.json`、`private_key_sync_v1.golden.json` 经由 C++ 与 C# 测试工程逐字节解包解密校验无误。
- **重写合规性**：Core `SyncDocumentReader` → `SyncDocumentWriter` 重写后的 JSON 文档 100% 通过桌面端 Zod Schema 严格校验。
- **互通矩阵**：10 大核心同步场景已在 `doc/INTEROP-REPORT.md` 规范化建档，覆盖初始化、增量同步、双向冲突合并、密钥轮换与多端并发。

---

## 5. 真机验收待办（Section 12）清点与处置矩阵

对 `04-TASKS.md` 第 12 节「真机验收待办」的全部条目进行了清查与分类处置：

| 类别 | 项数 | 处置说明 |
|---|---|---|
| **A. 自动化已覆盖** | 18 项 | 如桌面端互通向量校验（S04/S15）、单元测试与错误码对拍（N08/F01）、无障碍标签（Q05）、中文字面量门禁（Q04/O12/O13）、订阅配对门禁（O03/O05）等，已进入持续集成每日门禁。 |
| **B. 既往实测已通过** | 10 项 | X01（运行）、X02（版本号）、SP01（Release/ARM Spike 报告）、SP02（OpenSSL 3.6.3）、SP03（局域网 SSH 连通 697ms）、SP04（28组FPS矩阵）、SP05（软键盘中英文输入）、X04（日志脱敏）等均已真机完成并入档。 |
| **C. 交付用户实机回归** | 16 项 | 涉及外设（Continuum 拓展坞、蓝牙键盘）、特定网络（SIM 蜂窝网络切网、长稳 4 小时）、外部受管环境（讲述人辅助、从零全新设备侧载体验）。相关页面（`DevToolsPage` / `PerfPage` / `TokenGalleryPage`）已常驻并附带详细操作指引（见 `doc/INSTALL.md`、`doc/PERF-REPORT.md`、`doc/SECURITY-REPORT.md`）。 |

---

## 6. 构建产物归档与完整性验证

1. **全量自动化验证指令**：
   ```powershell
   pwsh scripts/verify.ps1 -Arm -Interop
   ```
   - ① dotnet test (Core): 1496 测试通过
   - ② native cmake + ctest: 316 测试通过
   - ③ 错误码对拍: 29 码严格一致
   - ④a 文档完整性: 15 份文档结构完好，127 任务全部闭合
   - ④ XAML 魔法数字: 零违例
   - ④b 硬编码文案: 零违例
   - ④c 生命周期卫生: 零违例
   - ⑤ App x64 Debug: 成功构建
   - ⑥ ARM Release: .NET Native 编译链接成功，无 WMC0151
   - ⑦ 跨端互通向量: 4 项检查全绿
2. **正式 Release 包结构**：
   - 路径：`artifacts/release/SshTool_1.0.0.0_ARM/`
   - 主程序包：`SshTool.App_1.0.0.0_ARM.appx`（已自签名）
   - 证书文件：`LumiaSshDev.cer`（受信任根证书）
   - 框架依赖：
     - `Dependencies/arm/Microsoft.NET.Native.Framework.1.7.appx`
     - `Dependencies/arm/Microsoft.NET.Native.Runtime.1.7.appx`
     - `Dependencies/arm/Microsoft.VCLibs.ARM.14.00.appx`
   - 安装说明：`README-INSTALL.txt`

---

## 7. 发布结论

**结论**：Lumia SSH v1.0.0 已达到既定设计与发布标准，代码架构稳定，安全与性能达标，测试全绿，具备正式归档与侧载发布条件。
