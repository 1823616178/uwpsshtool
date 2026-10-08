# Lumia SSH（uwpsshtool）

在 **Lumia 950 / 950 XL（Windows 10 Mobile，ARM32）** 上运行的原生 **SSH 终端客户端**（UWP：C# 7.3 + XAML + Win2D，原生层 C++/CX）。
目标是把一台 2015 年的手机变成趁手的移动终端：完整的 SSH / SFTP / 端口转发、多会话与分屏、中文输入与触控键条、
云端同步；并与桌面端、鸿蒙端两个私有客户端**共用同一账号、同一保险库、同一份端到端加密的同步文档**。

- 应用名 / 包标识：Lumia SSH / `SshTool.LumiaSsh`，版本 `1.0.0.0`（Tag `v1.0.0`）
- 平台：Windows 10 Mobile 10.0.15063+（ARM32 正式发布）、Windows 10 UWP（x64 开发与 Continuum）
- 状态：里程碑 M0–M10 与收尾系列（O / W / G）全部完成，验证流水线 8 步全绿；
  仍在真机上待验收的项集中在 [`doc/04-TASKS.md`](doc/04-TASKS.md) 末尾「真机验收待办」

## 功能

**SSH 内核（C++/CX 原生层）**
- 基于 libssh2 + OpenSSL 的异步非阻塞内核：会话状态机、连接 / 认证 / 通道、统一错误码
- 已回移上游安全补丁（见 `native/third_party/PATCHES.md`）
- 密码 / 公钥 / keyboard-interactive 认证；已知主机 TOFU 与指纹校验；多级跳板（ProxyJump）
- 保活与断线重连策略、网络切换检测、后台保活（ExtendedExecution）

**终端**
- libvterm 网格 + Win2D 脏行重绘；回滚缓冲、全文查找、链接识别
- KeyBar 触控键条（42 键，修饰键三态）；软键盘直通与中文输入；光标闪烁；响铃（振动 / 闪屏）
- tmux 自动附着与初始命令（AutoRun）

**会话与窗口**
- 会话管理：多标签、分屏窗格树、冷启动会话恢复、断线重连回现场
- 宽屏工作区（Continuum）：左右主从、多窗格拖拽调比例；窄屏只渲染聚焦窗格

**数据与安全**
- 凭据经 DPAPI 整段加密落盘（`secrets.bin`）；日志全量脱敏（不记录密码、私钥、终端内容等）
- 应用锁（冷启动锁 + 后台超时锁）、后台断线通知；详见 [`doc/SECURITY-REPORT.md`](doc/SECURITY-REPORT.md) 与 [`doc/PRIVACY.md`](doc/PRIVACY.md)

**云端同步（与桌面端 / 鸿蒙端互通）**
- 严格遵循桌面端 `SyncDocumentV1` 协议（零偏差），端到端加密：Argon2id + AES-256-GCM
- 双向增量同步、三向冲突合并、密码轮换与恢复密钥
- 本机专有数据（分组归属、外观、known_hosts 等）不上云、下行不清空；规范见 [`doc/03-SYNC-PROTOCOL.md`](doc/03-SYNC-PROTOCOL.md)

**密钥 / 文件 / 转发**
- 密钥：Ed25519 / RSA 生成与导入、指纹与 randomart 校验、ssh-agent
- SFTP：浏览 / 上传 / 下载 / 权限 / 书签、「用其他应用打开」、系统分享上传、ssh_config 导入
- 端口转发：本地 / 远程 / 动态（SOCKS5），运行时长与速率统计；多跳跳板（ProxyJump）

**外观与体验**
- 主题（深 / 浅 / 跟随系统）、强调色、终端配色（8 套内置 + 自定义 + iTerm2 / Windows Terminal 主题导入）
- 字号缩放、键条布局编辑、快捷键编辑、片段（Snippet）与变量、收藏与最近、开始屏幕磁贴、`ssh://` 唤起
- 中文 / English 双语、200% 文本缩放、无障碍（讲述人）

## 目录结构

| 路径 | 内容 |
|---|---|
| `src/SshTool.App` | UWP 应用（C# 7.3 + XAML + Win2D）：页面、自研控件、主题 Token |
| `src/SshTool.Core` | netstandard1.4 共享库：模型、同步、纯逻辑（宿主可测） |
| `src/SshTool.Native` | C++/CX 原生层（SSH / SFTP / 转发 / 终端网格），产出 winmd |
| `native/tests` | 原生层 googletest 测试（CMake） |
| `tests/SshTool.Core.Tests` | Core 单元测试（xUnit，宿主 net8） |
| `scripts` | 门禁（verify）、打包、部署、依赖抓取脚本 |
| `doc` | 设计 / 协议 / 任务 / 报告的完整文档，索引见 [`doc/README.md`](doc/README.md) |

## 构建

全量构建是**两段式**（原因见 [`doc/ENV.md`](doc/ENV.md)）：Native 需 VS2017（v141 工具集，ARM32），
C# 由 VS2026 构建（ARM Release 走 .NET Native）。

```pwsh
# 第三方依赖（libssh2 / libvterm / argon2 等）不入库，由脚本抓取并打补丁
pwsh scripts/fetch-third-party.ps1

# 用 vswhere 定位两套 MSBuild
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuildV141 = & $vswhere -version '[15.0,16.0)' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
$msbuildSln  = & $vswhere -version '[17.0,)'    -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1

# 1) 原生层（示例 x64 Debug；真机为 -p:Platform=ARM -p:Configuration=Release）
& $msbuildV141 src/SshTool.Native/SshTool.Native.vcxproj -p:Configuration=Debug -p:Platform=x64

# 2) C# 全部（App 直接引用原生层的 winmd 产物）
& $msbuildSln SshTool.sln -p:Configuration=Debug -p:Platform=x64
```

## 验证

```pwsh
pwsh scripts/verify.ps1        # 全量门禁，8 步
```

| 步骤 | 内容 |
|---|---|
| ① | Core 单元测试（`dotnet test`，1500+ 条） |
| ② | native 宿主机测试（`cmake` + `ctest`，300+ 条） |
| ③ | 错误码对拍（C# ↔ C++ 逐项一致） |
| ④a | 文档完整性守卫（doc/*.md 与任务条目计数） |
| ④ | XAML 魔法数字（颜色 / 字号 / 间距必须走 Token） |
| ④b | 硬编码文案棘轮（可见文案必须资源化，只减不增） |
| ④c | 生命周期卫生（订阅配对、fire-and-forget 必须可观察） |
| ⑤ | App x64 Debug 两段式构建（含 WMC0151 的 15063 契约检查） |

## 打包与安装到手机

```pwsh
pwsh scripts/package-arm.ps1 -Sign    # 产出 artifacts/release/SshTool_<版本>_ARM/（已签名 appx + 依赖 + 证书）
pwsh scripts/deploy-phone.ps1         # WinAppDeployCmd / Device Portal 侧载
```

手机需开启开发人员模式；首次安装需信任自签名证书（`CN=LumiaSshDev`）。完整步骤见
[`doc/INSTALL.md`](doc/INSTALL.md)。

## 文档

| 文档 | 内容 |
|---|---|
| [`doc/README.md`](doc/README.md) | 文档索引与「AI 分次编码工作流」 |
| [`doc/01-DESIGN.md`](doc/01-DESIGN.md) | 总体设计：架构、线程模型、原生层接口、安全、测试 |
| [`doc/02-UI-DESIGN.md`](doc/02-UI-DESIGN.md) | UI 设计：Design Token、自适应规则、逐页线框 |
| [`doc/03-SYNC-PROTOCOL.md`](doc/03-SYNC-PROTOCOL.md) | 同步协议兼容规范（`SyncDocumentV1`） |
| [`doc/04-TASKS.md`](doc/04-TASKS.md) | 任务编排、进度日志、真机验收待办 |
| [`doc/INSTALL.md`](doc/INSTALL.md) | 侧载与安装指南（证书、USB / Wi-Fi 部署、升级） |
| [`doc/ENV.md`](doc/ENV.md) | 本机构建环境与两段式工具链说明 |
| [`doc/SECURITY-REPORT.md`](doc/SECURITY-REPORT.md) / [`doc/PRIVACY.md`](doc/PRIVACY.md) | 安全自查报告 / 隐私说明 |
| [`doc/PERF-REPORT.md`](doc/PERF-REPORT.md) / [`doc/RELEASE-CHECKLIST.md`](doc/RELEASE-CHECKLIST.md) | 性能实测报告 / 发布前总检清单 |

## 说明

- 第三方组件（libssh2、libvterm、argon2、OpenSSL、字体等）的来源与许可清单见
  [`doc/OPEN_SOURCE_LICENSES.md`](doc/OPEN_SOURCE_LICENSES.md)；第三方源码不入库，由 `scripts/fetch-third-party.ps1` 抓取。
- 仓库不含任何密钥、凭据与同步明文；日志与诊断遵循脱敏规范。
- 本仓库与桌面端、鸿蒙端两个客户端共用同一套同步格式，但彼此为独立（私有）仓库。
