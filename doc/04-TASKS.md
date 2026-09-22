# Lumia SSH 任务编排

> 配套：`01-DESIGN.md`（设计）、`02-UI-DESIGN.md`（界面）、`03-SYNC-PROTOCOL.md`（同步）。
> 工作流见 `README.md`。**每次只做一个任务。**

---

## 0. 使用说明

### 0.1 标记

| 标记 | 含义 |
|---|---|
| `- [ ]` / `- [x]` | 任务或验收项 未完成 / 完成 |
| `S` / `M` | 预估规模：S ≈ 半次 AI 会话；M ≈ 一次 AI 会话（超过就按 a/b 拆开） |
| 📱 | 需要在 Lumia 950 真机上验证，AI 不能自行勾选 |
| 👤 | 需要人工操作（设备设置、提供测试服务器、真机跑结果回填） |
| ⏳ | 写在进度日志里：AI 已完成可自动部分，正在等待 👤/📱 结果 |

### 0.2 任务完成定义（DoD）

1. 「产出」列出的文件全部存在，内容满足「要点」。
2. 「验收」中所有可自动验证的代码、测试与构建项已勾选。
3. `pwsh scripts/verify.ps1` 全绿（X06 之前按任务「验证」一栏执行）。
4. 📱/👤 项未验证 → 原样抄到本文末「真机验收待办」，不阻塞任务完成与后续依赖。
5. 勾选任务行 → 更新「进度总览」→ 追加「进度日志」→ `git commit`。

### 0.3 选择下一个任务的规则

- 按本文**从上到下**顺序，找第一个「依赖全部 `[x]`、自身 `[ ]`」的任务。
- 若该任务含 👤/📱 步骤且人工结果尚未提供：完成代码、自动测试、页面、脚本与操作指南，把人工项登记到「真机验收待办」，**勾选代码任务并继续开发**。开发阶段不逐项打断用户；按里程碑批量回归。
- Spike（`SP*`）的结论会改设计：记录结论后必须回写 `01-DESIGN.md` 对应决策行。

---

## 1. 进度总览

| 里程碑 | 内容 | 任务数 | 已完成 | 出口演示 |
|---|---|---|---|---|
| M0 | 基座与技术验证 | 13 | 13 | 空应用在 Lumia 运行并调用 native；6 个 Spike 结论入档 |
| M1 | 原生 SSH 内核 | 11 | 11 | 调试页在 Lumia 上连服务器执行命令看到输出 |
| M2 | 终端引擎、渲染与输入 | 15 | 15 | 调试页里跑 vim/htop，键条、选择复制、滚动缩放可用 |
| M3 | 数据层与主机管理 | 12 | 12 | 主机/分组增删改、凭据安全保存 |
| M4 | 终端页与会话 | 12 | 12 | **完整可用的本地 SSH 客户端**（无同步） |
| M5 | 云端同步 | 22 | 22 | 与桌面端同账号双向同步、冲突可解 |
| M6 | 外观系统 | 5 | 5 | 主题、字体、配色可改可导入 |
| M7 | 密钥、SFTP、转发、跳板 | 11 | 11 | 密钥管理、传文件、开隧道、跳板连接、私钥同步 |
| M8 | 打磨与发布 | 11 | 6 | 性能/安全报告、可侧载安装包 v1.0.0 |
| M9 | 优化与债务清理 | 15 | 10 | 泄漏归零、热路径提速、文案门禁生效（依据 `06-OPT-AUDIT.md`） |
| **合计** | | **127** | **117** | |

### 1.1 关键路径

```
X01 → X02 → SP02 → SP03 → N01 → N02 → N03 → N04 → N05 → N06 → N07 → N08 → N09a → N09b → T03
    ├→ T04 → T05 → T06 → T07 → T09 → T10 ─┐
    └→ D06（另需 D02、D03）───────────────┴→ U07 → P01 → P02 → S14 → U15 → U16 → U17 → U18/U19/U20 → S16 → Q03 → Q10
并行支线（只依赖 X02，可穿插）：T08、U11、D04、D01 → D02 → S01 → S02 → S09、S06 → S07
```

---

## 2. M0 — 基座与技术验证

- [x] **X01 开发环境与真机部署打通** `S` 👤📱
  - 依赖：—
  - 参考：`01-DESIGN.md §1.2`
  - 产出：`doc/ENV.md`
  - 要点：
    1. 列出并核对：VS2019 16.11（UWP 开发、C++ v142 UWP 工具、ARM 编译器）、Windows SDK 10.0.17763（及 19041）、.NET 8 SDK（Core 测试）、CMake ≥3.25、Git、Perl（OpenSSL 备用构建）、Node 20+（sync-vectors 工具）。
    2. 手机：设置 → 更新和安全 → 开发者选项 → 开发人员模式 + 设备发现 + 设备门户；记录系统版本（设置 → 关于，如 10.0.15254.x）。
    3. 在临时目录（不入库）新建空白 C# UWP（min 15063 / target 17763），ARM Release 构建，用 `WinAppDeployCmd.exe devices` 与 `install -file <appx> -ip <ip> -pin <pin>` 安装（依赖包目录一并安装）。
    4. 记录 VS2022 能否部署到该手机（能/不能/未测）。
  - 验收：
    - [x] `doc/ENV.md` 齐全：工具版本（§1）、手机 OS build 10.0.15254.603 与设备事实（§2）、部署命令与设备门户配对/取文件/装包（§3、§3.1）、踩坑 9 条（§5）
    - [x] 📱 应用在 Lumia 950 启动成功（2026-09-18 多次，app.log 与四份调试页报告为证；已远超「空白应用」）
  - 验证：人工

- [x] **X02 仓库与解决方案骨架** `M`
  - 依赖：X01
  - 参考：`01-DESIGN.md §4、§5`
  - 产出：`.gitignore`、`.gitattributes`、`CLAUDE.md`、`Directory.Build.props`、`SshTool.sln`、`src/SshTool.Core/SshTool.Core.csproj`、`tests/SshTool.Core.Tests/SshTool.Core.Tests.csproj`、`src/SshTool.Native/SshTool.Native.vcxproj`（+ `NativeInfo.h/.cpp`、`pch.*`）、`src/SshTool.App/SshTool.App.csproj`（+ `App.xaml`、`MainPage.xaml`、`Package.appxmanifest`）
  - 要点：
    1. `git init`；`.gitignore` 覆盖 `bin/ obj/ .vs/ *.user AppPackages/ BundleArtifacts/ packages/ node_modules/ native/prebuilt/ native/tests/build/`，`native/third_party/*/`（保留 `native/third_party/PATCHES.md`）。
    2. `Directory.Build.props`：`LangVersion=7.3`、统一 NuGet 版本属性（UWP 包、Newtonsoft.Json 12.0.3、Win2D 暂空）。
    3. Core：SDK 风格 `netstandard1.4`，`TreatWarningsAsErrors=true`，放一个 `CoreInfo.Version` 常量。
    4. Core.Tests：`net8.0` + xUnit，一个断言 `CoreInfo.Version` 的测试。
    5. Native：C++/CX Windows 运行时组件，平台 ARM/x86/x64，`NativeInfo::Version()` 返回 `"0.0.1"`。
    6. App：UWP C#（PackageReference 格式），`TargetPlatformMinVersion=10.0.15063.0`、`TargetPlatformVersion=10.0.19041.0`（X01 实测：本机无 17763 SDK，见 `doc/ENV.md`），引用 Core；Native 因 VS2026 无 v141/UWP 工具，改为先由 VS2017(v141) 单独构建、App 直接引用其 winmd 产物；MainPage 显示 `CoreInfo.Version` 与 `NativeInfo.Version()`；清单能力 `internetClient`、`internetClientServer`、`privateNetworkClientServer`；显示名「Lumia SSH」；添加 Windows Mobile Extensions for the UWP 引用。
    7. `CLAUDE.md`：一段项目定位 + 「开工先读 `doc/README.md`」+ 硬性约束摘要 + 构建/测试命令。
  - 验收：
    - [x] `dotnet test tests/SshTool.Core.Tests` 通过
    - [x] `msbuild SshTool.sln /p:Configuration=Debug /p:Platform=x64` 成功
    - [x] `msbuild SshTool.sln /p:Configuration=Release /p:Platform=ARM` 成功（.NET Native）
    - [x] 目录结构与 `01-DESIGN.md §5` 一致（尚未用到的目录可不建）
    - [ ] 📱 应用在 Lumia 上显示两个版本号
  - 验证：上述命令

- [x] **SP01 Spike：.NET Native + netstandard1.4 + Newtonsoft 真机可用性** `S` 👤📱
  - 依赖：X02
  - 参考：`01-DESIGN.md D7、D9、R7`
  - 产出：`src/SshTool.Core/Spikes/JsonSpike.cs`、`src/SshTool.App/Views/Debug/SpikePage.xaml(.cs)`（Debug 构建入口）、`doc/ENV.md` 追加结论
  - 要点：
    1. `JsonSpike.RoundTrip()`：用 `JsonTextWriter` 按固定键序写一个含字符串/整数/布尔/null/数组/嵌套对象的 JSON，再用 `JsonTextReader`→`JObject` 读回并逐字段比较，返回报告字符串。
    2. SpikePage 按钮调用并显示结果；另测 `ulong.Parse("18446744073709551615")`、`DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", InvariantCulture)`。
    3. 先用 `Microsoft.NETCore.UniversalWindowsPlatform 6.2.14`；ARM Release 部署或运行失败则降为 5.4.x 重试，记录现象。
  - 验收：
    - [x] Core 单测覆盖 `JsonSpike.RoundTrip()` 通过
    - [x] 📱 ARM Release 真机报告全部一致（2026-09-18 用户回报：.NET Native/ARM Release 包 9/9 全 PASS，与宿主机 x64 Debug 逐项一致）
    - [x] `Directory.Build.props` 锁定最终 UWP 包版本（`NetCoreUwpVersion` = 6.2.14，无需降 5.4.x）；`01-DESIGN.md` D9 与 §1.2 已回写
  - 验证：`dotnet test`；真机

- [x] **SP02 Spike：OpenSSL 编成 ARM-UWP 静态库并链入 native 组件** `M` 👤📱
  - 依赖：X02
  - 参考：`01-DESIGN.md D16、R1`；鸿蒙端 `docs/NATIVE-BUILD.md`
  - 产出：`scripts/build-openssl.ps1`、`native/NATIVE-BUILD.md`、`native/prebuilt/{arm-uwp,x86-uwp,x64-uwp,x64-windows-static}/`（不入库）、`NativeInfo::OpenSslVersion()`
  - 要点：
    1. 方案 A：vcpkg（固定 commit 写进脚本）安装 `openssl:arm-uwp`、`openssl:x86-uwp`、`openssl:x64-uwp`、`openssl:x64-windows-static`（宿主机测试用），拷贝 include/lib 到 `native/prebuilt/<triplet>`。
    2. 方案 A 失败 → 方案 B：OpenSSL 源码 `perl Configure VC-WIN32-ARM-UWP no-shared no-tests no-apps no-docs no-engine no-legacy` + `nmake`（x86/x64 同理 `VC-WIN32-ONECORE`/`VC-WIN64A-UWP`）；3.x 不行再试 1.1.1w。
    3. Native 项目按平台链接 `libcrypto.lib`，`OpenSslVersion()` 返回 `OpenSSL_version(OPENSSL_VERSION)`；MainPage 显示。
    4. `NATIVE-BUILD.md` 记录：采用方案、版本、完整命令、耗时、产物大小、遇到的补丁。
  - 验收：
    - [x] 脚本可一键重跑，四个目标产物齐全
    - [x] 应用显示 OpenSSL 版本（真机 ARM Release 实测，证据强于 x64 Debug）
    - [x] ARM Release 构建成功
    - [x] 📱 Lumia 上显示 OpenSSL 版本：真机 app.log 记录 `OpenSSL: OpenSSL 3.6.3 9 Jun 2026`（v0.1.0.3 / Arm Release / .NET Native，2026-09-18）
    - [x] `01-DESIGN.md` D16/R1 回写结论
  - 验证：`pwsh scripts/build-openssl.ps1`；msbuild；真机

- [x] **SP03 Spike：libssh2 真机连接真实服务器** `M` 👤📱
  - 依赖：SP02
  - 参考：`01-DESIGN.md D2、§6.1、R2`
  - 产出：`scripts/fetch-third-party.ps1`（先只含 libssh2 1.11.1，固定 URL + SHA256）、`native/third_party/PATCHES.md`、`src/SshTool.Native/Spike/SshSpike.{h,cpp}`、SpikePage 增加「SSH 测试」区
  - 要点：
    1. 下载解压 libssh2 到 `native/third_party/libssh2`；Native 项目编译其 `src/*.c`，定义 `LIBSSH2_OPENSSL`，提供 UWP 用 `libssh2_config.h`（`HAVE_*` 宏）；遇到 UWP 禁用 API 用宏或小补丁解决并记录到 `PATCHES.md`。
    2. `SshSpike::ExecAsync(host, port, user, password, command)`：后台线程里 `WSAStartup` → `getaddrinfo` → 阻塞 connect → `libssh2_session_handshake` → `userauth_password` → `channel_exec` → 读 stdout → 返回文本 + `libssh2_session_methods` 协商的 KEX/HOSTKEY/CIPHER。
    3. 同时测试：本进程回环 UDP socket 对（`127.0.0.1` 绑定 + 互发 1 字节）能否工作（EventLoop 唤醒方案依据）。
    4. 👤 提供测试服务器：局域网 Linux（或 WSL `sshd` 端口转发到局域网）。
  - 验收：
    - [x] x64 Debug 在 PC 上对测试服务器执行 `uname -a` 返回正确（2026-09-18，无人值守钩子实测，报告 sp03-ssh-20260918-084849.txt；kex curve25519-sha256 / hostkey ecdsa-sha2-nistp256 / cipher chacha20-poly1305 / mac hmac-sha2-256，总耗时 652ms）
    - [x] 📱 Lumia 经 Wi-Fi 对局域网服务器执行成功：697 ms；算法与 PC 完全一致；同一服务器指纹一致（2026-09-18）
    - [ ] 📱 回环 UDP 唤醒对结果已记录（可用/不可用）
    - [x] `PATCHES.md` 已记录 libssh2 零补丁结论；UDP 唤醒方案待渐进真机回归后回写 `01-DESIGN.md` §6.1
  - 验证：真机

- [x] **SP04 Spike：Win2D 终端渲染帧率** `M` 👤📱
  - 依赖：X02
  - 参考：`01-DESIGN.md D5、§7.3、§7.4、R3`
  - 产出：`src/SshTool.App/Views/Debug/RenderSpikePage.xaml(.cs)`、`Assets/Fonts/JetBrainsMono-Regular.ttf`、`Assets/Fonts/JetBrainsMono-Bold.ttf`、`Assets/Fonts/OFL.txt`、`doc/ENV.md` 追加结论
  - 要点：
    1. 添加 `Win2D.uwp`：从 1.26.0 起试，若要求 min > 15063 则依次降到支持 15063 的版本，锁定到 `Directory.Build.props`。
    2. 页面：`CanvasControl` 绘制字符网格（模式：48×30、88×24），随机字符 + 随机 16 色前景/背景；三种负载：全屏每帧重绘、每帧 3 行脏行（行缓存 `CanvasRenderTarget` + 局部重绘）、静止。
    3. 帧驱动 `CompositionTarget.Rendering`，叠加 FPS 与帧耗时显示；另一模式同时放两个 CanvasControl。
    4. 字体：`ms-appx:///Assets/Fonts/JetBrainsMono-Regular.ttf#JetBrains Mono`；中文回退依次尝试 `Microsoft YaHei UI`、`DengXian`、`SimSun`，显示哪个生效。
  - 验收：
    - [x] 页面可运行（x64 Debug 构建通过；真机 ARM Release 上完整跑完 28 组矩阵，证据更强）
    - [x] 📱 记录负载 × 尺寸 × 单/双实例 的 FPS（28 组，报告 sp04-render-20260918-020955.txt，ENV.md §8）
    - [x] 📱 记录可用中文字体名（Microsoft YaHei UI）
    - [x] `01-DESIGN.md` D5、§7.3、§7.4 已回写：**保持方案，无需兜底**；新增「逐格绘制是禁区、run 合并必需、每画布上限 ~30 draw/s」三条硬性纪律
  - 验证：真机

- [x] **SP05 Spike：软键盘、中文输入法与物理键盘事件** `S` 👤📱
  - 依赖：X02
  - 参考：`01-DESIGN.md §7.5、R4`
  - 产出：`src/SshTool.App/Views/Debug/InputSpikePage.xaml(.cs)`、`doc/ENV.md` 追加事件序列记录
  - 要点：
    1. 隐藏 TextBox 哨兵法原型；把 `KeyDown`、`BeforeTextChanging`（若存在）、`TextChanging`、`TextChanged`、`TextCompositionStarted/Changed/Ended`、`SelectionChanged` 按时间顺序写入日志列表。
    2. `InputPane.Showing/Hiding` 记录 `OccludedRect`；`CoreWindow.KeyDown/CharacterReceived`、`AcceleratorKeyActivated` 记录（蓝牙键盘或 Continuum 键盘）。
    3. 👤 在手机上依次操作：英文输入 `ls -la`、在哨兵上连按退格、回车、拼音输入「你好」并选词、点联想词、输入 emoji、切换输入法。
  - 验收：
    - [x] 📱 各操作的事件序列已记录（中文/英文两轮，报告 sp05-input-20260918-022625 与 -023117）
    - [x] 组合态判定与提交策略五条已定稿回写 `01-DESIGN.md §7.5`；**300 ms 静默兜底不启用**
  - 验证：真机

- [x] **SP06 Spike：后台保活、常亮、DPAPI 与明文 HTTP** `S` 👤📱
  - 依赖：SP03
  - 参考：`01-DESIGN.md §10、D10、D11、R5、R6`
  - 产出：`src/SshTool.App/Views/Debug/PlatformSpikePage.xaml(.cs)`、`doc/ENV.md` 追加结论
  - 要点：
    1. 建立一条 SSH 连接（复用 SshSpike，改为保持 shell + 每 10 s 发一次 `echo tick`），记录收到回显的时间戳。
    2. 请求 `ExtendedExecutionSession(Reason=Unspecified)`，记录结果与 `Revoked` 事件及原因；`DisplayRequest.RequestActive/Release`。
    3. `DataProtectionProvider("LOCAL=user")` 加密/解密 1 KB 数据往返；重启应用后解密此前写入的文件。
    4. `Windows.Web.Http.HttpClient`（关缓存、关 Cookie）请求 `http://123.161.179.32:46926/api/v1/me`（期望 401 JSON），再发 HEAD `sync/document`（期望 401），显示状态码、头、体；测试 `If-Match: "revision-0"` 头能否添加。
    5. 👤 操作：锁屏 1/5/15 分钟、切到其他应用、开启省电模式，分别记录连接存活情况。
  - 验收：
    - [ ] 📱 ExtendedExecution 授予/撤销情况与各场景存活时长已记录
    - [ ] 📱 DPAPI 往返与重启后解密成功
    - [ ] 📱 HTTP 401 响应体与头解析正确，If-Match 可发送
    - [ ] 📱 真机结论出来后回写 `01-DESIGN.md §10`、D10、D11、R5、R6
  - 验证：真机

- [x] **X03 Design Token 与主题资源** `M`
  - 依赖：X02
  - 参考：`02-UI-DESIGN.md §2、§3`
  - 产出：`src/SshTool.App/Themes/{Tokens.xaml,Tokens.Dark.xaml,Tokens.Light.xaml,Controls.xaml}`、`src/SshTool.App/Platform/ThemeService.cs`、`src/SshTool.App/Views/Debug/TokenGalleryPage.xaml(.cs)`、`scripts/check-magic-numbers.ps1`
  - 要点：
    1. 按 UI §2.1–2.4 定义全部键；深浅色放 `ResourceDictionary.ThemeDictionaries`；`App.xaml` 合并。
    2. `ThemeService.Apply(mode)`：设置根 Frame `RequestedTheme`；`UseSystemAccent` 时用 `UISettings.GetColorValue(Accent)` 覆盖 `AppAccentBrush`，监听 `ColorValuesChanged`。
    3. 画廊页展示所有颜色块、字号、间距、图标字形，带深浅色切换。
    4. `check-magic-numbers.ps1`：扫描 `src/SshTool.App/{Views,Controls,Dialogs}/**/*.xaml`，发现 `#[0-9A-Fa-f]{6,8}`、`FontSize="\d`、`Margin="\d`、`Padding="\d`、`Width="\d`（`Width="*"`/`Auto` 除外）即报错，`Themes/` 与 `Views/Debug/` 豁免。
  - 验收：
    - [ ] x64 Debug 构建通过，画廊页深浅色切换正确（构建已过；切换观感需运行 UWP，👤 PC 部署或 📱 确认）
    - [x] 检查脚本对故意写入的违规返回非 0，移除后返回 0
  - 验证：msbuild；`pwsh scripts/check-magic-numbers.ps1`

- [x] **X04 日志、脱敏与错误码基础** `M`
  - 依赖：X02
  - 参考：`01-DESIGN.md §6.3、§12.2`；鸿蒙端 `entry/src/main/ets/common/SshError.ets`、`common/utils/Logger.ets`
  - 产出：`src/SshTool.Core/Common/{ILogger.cs,LogLevel.cs,LogRedactor.cs,LogRotationPlanner.cs,SshErrorCode.cs}`、`src/SshTool.App/Platform/FileLogger.cs`、`src/SshTool.App/Strings/zh-CN/Resources.resw`、`src/SshTool.App/Strings/en-US/Resources.resw`、`tests/SshTool.Core.Tests/Common/*Tests.cs`
  - 要点：
    1. `SshErrorCode` 枚举数值与 §6.3 完全一致（含 405 策略性断开、999）。
    2. `LogRedactor.Redact(string)`：键名匹配（大小写不敏感）`password|passphrase|token|accessToken|refreshToken|privateKey|recoveryKey|ciphertext|authorization|syncPassword` 的 `key=value`、`"key":"value"` 形式整值替换为 `***`；`-----BEGIN ... -----END ...` 块、`SPM1-[A-Za-z0-9_-]{43}-[A-Fa-f0-9]{12}`、`Bearer\s+\S+` 替换。
    3. `LogRotationPlanner`：给定当前大小、上限 1 MiB、保留 3 → 返回需要执行的重命名/删除步骤（纯逻辑可测）。
    4. `FileLogger`：后台队列写 `LocalFolder/logs/app.log`，格式 `yyyy-MM-dd HH:mm:ss.fff [LEVEL] [Tag] message`，写前必过 Redactor。
    5. resw：每个错误码 `Error_<数值>` 中英文文案（参考鸿蒙端 `SshError.ets` 的中文）。
  - 验收：
    - [x] Redactor 单测覆盖上述每种模式（≥12 条）
    - [x] RotationPlanner 单测覆盖未超限/超限/已有 3 个文件
    - [x] 单测读取两个 resw（文件路径相对仓库根）校验每个 `SshErrorCode` 值都有 `Error_<n>` 键
    - [x] 📱 应用启动后日志文件生成：2026-09-18 从真机（v0.1.0.1/ARM Release）经设备门户取回 `LocalState\logspp.log`，91 行，格式逐字符符合 `yyyy-MM-dd HH:mm:ss.fff [LEVEL] [Tag] message`，无敏感词命中
  - 验证：`dotnet test`；msbuild

- [x] **X05 应用配置与清单** `S`
  - 依赖：X02
  - 参考：`01-DESIGN.md §1.1、§12.3`
  - 产出：`src/SshTool.App/Config/{appconfig.Debug.json,appconfig.Release.json}`、`src/SshTool.App/Platform/AppConfig.cs`、`Package.appxmanifest` 更新
  - 要点：
    1. 字段：`syncApiBaseUrl`（两套均为 `http://123.161.179.32:46926`）、`allowHttp`（true）、`logLevel`（debug/info）；csproj 按 Configuration 把对应文件复制为 `Assets/appconfig.json`（Content）。
    2. `AppConfig.LoadAsync()` 读包内文件（`Windows.ApplicationModel.Package.Current.InstalledLocation`），手写 JObject 解析，缺字段用默认值并记警告。
    3. 清单：方向全部支持；能力三项；应用版本 `0.1.0.0`；不包含任何服务端密钥。
  - 验收：
    - [x] Debug/Release 构建后包内 `appconfig.json` 分别对应
    - [x] Core 或 App 层解析逻辑有单测（解析函数放 Core：`AppConfigParser`）
  - 验证：`dotnet test`；msbuild 两种配置

- [x] **X06 测试与门禁脚本** `M`
  - 依赖：X03、X04
  - 参考：`01-DESIGN.md §13`
  - 产出：`scripts/verify.ps1`、`native/tests/CMakeLists.txt`、`native/tests/smoke_test.cpp`、`native/core/core_info.{h,cpp}`、`scripts/check-error-codes.ps1`（先做成：C++ 头不存在时跳过并提示）
  - 要点：
    1. `native/tests` 用 CMake + FetchContent(GoogleTest 1.14)，编译 `native/core` 下源文件为静态库 `ssh_core_host`，冒烟测试调用 `core_info_version()`。
    2. `verify.ps1` 步骤：① `dotnet test`；② `cmake -S native/tests -B native/tests/build -A x64` + build + `ctest --output-on-failure`；③ `check-error-codes.ps1`；④ `check-magic-numbers.ps1`；⑤ `msbuild` App x64 Debug；参数 `-Arm` 追加 ARM Release 构建；参数 `-Quick` 只跑 ①②。
    3. 每步计时，最后打印汇总表；任何一步失败返回非 0。
    4. 自动查找 msbuild（`vswhere`）。
  - 验收：
    - [x] `pwsh scripts/verify.ps1` 全绿
    - [x] 人为让一个测试失败时脚本返回非 0 并指出失败步骤
  - 验证：`pwsh scripts/verify.ps1`

- [x] **X07 MVVM 与应用基础设施** `M`
  - 依赖：X03
  - 参考：`01-DESIGN.md D15`；`02-UI-DESIGN.md §4、§6`
  - 产出：`src/SshTool.Core/Mvvm/{ObservableObject.cs,RelayCommand.cs,AsyncCommand.cs,DialogQueue.cs}`、`src/SshTool.App/Infrastructure/{ServiceRegistry.cs,DispatcherHelper.cs,NavigationService.cs,IBackHandler.cs,DialogService.cs,ViewModelBase.cs}`、`src/SshTool.App/Controls/{StatusDot,Banner,EmptyState,SectionHeader,LoadingOverlay,TransientToast}.xaml(.cs)`、画廊页追加控件演示
  - 要点：
    1. `ObservableObject.SetProperty(ref field, value, [CallerMemberName])`；`RelayCommand`/`RelayCommand<T>`（`CanExecute` + `RaiseCanExecuteChanged`）；`AsyncCommand`（`IsRunning`、执行中禁用、异常交给注入的处理器）。
    2. `DialogQueue`：W10M 同一时刻只能显示一个 ContentDialog —— 纯逻辑队列（入队、完成出队、取消），App 的 `DialogService` 基于它显示。
    3. `NavigationService`：`Navigate<TPage>(param)`、`GoBack()`；订阅 `SystemNavigationManager.BackRequested`，按 `02-UI-DESIGN.md §4` 优先级依次询问注册的 `IBackHandler`（弹层 → 页面 → Frame）。
    4. 控件依赖属性与视觉按 UI §6；全部使用 Token。
  - 验收：
    - [x] Core 单测：SetProperty 通知、RelayCommand CanExecute、AsyncCommand 并发防重入与异常、DialogQueue 顺序与取消（≥10 条）
    - [ ] 画廊页展示 6 个控件的各状态（XAML 已就绪、构建过；观感需运行 UWP，👤/📱）
    - [x] `check-magic-numbers.ps1` 通过
  - 验证：`pwsh scripts/verify.ps1`

---

## 3. M1 — 原生 SSH 内核

- [x] **N01 第三方源码与原生构建体系** `M`
  - 依赖：SP02、SP03
  - 参考：`01-DESIGN.md §3.2、§5、D16、D17`
  - 产出：`scripts/fetch-third-party.ps1`（libssh2 1.11.1、libvterm 0.3.3、phc-winner-argon2 20190702，固定 URL + SHA256）、`native/core/NativeCore.vcxitems`（共享项目，列出 core 与 third_party 源文件）、`src/SshTool.Native` 引用共享项目并移除 Spike 内联源文件、`native/tests/CMakeLists.txt` 更新、`native/NATIVE-BUILD.md` 完整化、`native/tests/deps_smoke_test.cpp`
  - 要点：
    1. libvterm 的 `src/encoding/*.inc` 需由 `.tbl` 生成：脚本内用 Perl 生成或下载发布包中已生成文件，记录做法。
    2. argon2 只编译参考实现（`ref.c`，不用 `opt.c` SSE），`ARGON2_NO_THREADS`。
    3. 宿主 CMake 链接 `native/prebuilt/x64-windows-static` 的 OpenSSL。
    4. 冒烟测试：`libssh2_version(0)` 为 1.11.1；`vterm_new(24,80)` 写入 `"hi"` 后屏幕单元格正确；Argon2id 使用 RFC 9106 §5.3 官方测试向量结果一致。
  - 验收：
    - [x] SshTool.Native ARM/x86/x64 均构建成功
    - [x] 宿主机 native 测试 3 条冒烟用例通过
    - [x] `NATIVE-BUILD.md` 可让新机器从零复现
  - 验证：`pwsh scripts/verify.ps1 -Arm`

- [x] **N02 事件循环与会话线程** `M`
  - 依赖：N01
  - 参考：鸿蒙端 `cpp/io/EventLoop.*`、`SessionThread.*`、`cpp/tests/event_loop_test.cpp`、`session_thread_test.cpp`；`01-DESIGN.md §6.1`
  - 产出：`native/core/io/{EventLoop,SessionThread,WinsockInit}.{h,cpp}`、`native/tests/{event_loop_test,session_thread_test}.cpp`
  - 要点：`WSAPoll` 替代 epoll；唤醒机制按 SP03 结论（回环 UDP 对或 50 ms 超时轮询）；定时器最小堆；`WinsockInit` 进程级引用计数；线程安全的 `Post(task)`。
  - 验收：
    - [x] 移植的全部用例通过（Windows 不适用的用例写明原因并替换为等价用例）
    - [x] 1000 次创建/启动/停止后进程句柄数不增长（`GetProcessHandleCount` 断言）
  - 验证：`pwsh scripts/verify.ps1 -Quick`

- [x] **N03 SSH 会话生命周期与状态机** `M`
  - 依赖：N02
  - 参考：鸿蒙端 `cpp/ssh/session.*`、`cpp/tests/ssh_session_test.cpp`、`sshd_testkit.h`
  - 产出：`native/core/ssh/session.{h,cpp}`、`native/tests/ssh_session_test.cpp`、`native/tests/sshd_testkit.h`
  - 要点：状态 `Idle→Connecting→Handshaking→Authenticating→Established→Disconnected/Error`；非阻塞 connect（`WSAEWOULDBLOCK` + 可写判定 + `SO_ERROR`）；连接超时；优雅关闭；集成测试读取环境变量 `SSH_TEST_HOST/PORT/USER/PASSWORD`，未设置时跳过（`GTEST_SKIP`）。
  - 验收：
    - [x] 状态迁移单测全部通过（非法迁移被拒）
    - [x] 不可达地址在超时时间内进入 Error(102/104)，不挂死
    - [x] 设置环境变量时对 WSL sshd 完成握手（本地手动跑一次并在进度日志记录）
  - 验证：`pwsh scripts/verify.ps1 -Quick`

- [x] **N04 主机密钥与 TOFU** `S`
  - 依赖：N03
  - 参考：鸿蒙端 `cpp/ssh/hostkey.*`、`cpp/tests/hostkey_test.cpp`
  - 产出：`native/core/ssh/hostkey.{h,cpp}`、`native/tests/hostkey_test.cpp`
  - 要点：`SHA256:` + base64 无填充指纹；randomart（OpenSSH 同款 17×9）；密钥类型名；比对接口返回 Match/Mismatch/Unknown；Mismatch 时会话不得继续认证。
  - 验收：
    - [x] 固定公钥 blob 的指纹与 randomart 与 `ssh-keygen -lv` 输出一致（期望值写死在测试中）
    - [x] Mismatch 路径断言不进入 Authenticating
  - 验证：`pwsh scripts/verify.ps1 -Quick`

- [x] **N05 认证：密码 / 公钥 / keyboard-interactive** `M`
  - 依赖：N04
  - 参考：鸿蒙端 `cpp/ssh/auth.*`、`cpp/tests/auth_test.cpp`
  - 产出：`native/core/ssh/auth.{h,cpp}`、`native/tests/auth_test.cpp`、`native/tests/fixtures/keys/`（测试专用密钥：ed25519 OpenSSH 未加密/加密、RSA PEM 未加密/加密，WSL `ssh-keygen` 生成后提交）
  - 要点：`userauth_password`；`userauth_publickey_frommemory`（私钥缓冲 + 短语）；keyboard-interactive 回调通过 `IAuthPromptSink` 接口把 prompts 抛给上层并**阻塞等待**答复（条件变量，120 s 超时视为取消）；列出服务器支持的认证方式；缓冲用完 `OPENSSL_cleanse`。
  - 验收：
    - [x] 错误映射单测：认证失败 → 201/202/203，短语错误 → 204
    - [x] KI 回调在无答复超时后返回取消，不死锁
    - [ ] 集成（环境变量开启时）四种方式各连通一次，结果记入进度日志（已连通 2/4：公钥未加密/加密；密码与 KI 待真实凭据与 KI 服务器，见文末待办）
  - 验证：`pwsh scripts/verify.ps1 -Quick`

- [x] **N06 Shell 通道、PTY 与 exec** `M`
  - 依赖：N05
  - 参考：鸿蒙端 `cpp/ssh/channel.*`、`cpp/tests/channel_test.cpp`
  - 产出：`native/core/ssh/channel.{h,cpp}`、`native/tests/channel_test.cpp`
  - 要点：`request_pty(termType, cols, rows)`、`setenv`（失败忽略并记录）、`shell`、`request_pty_size`、读写非阻塞泵（EAGAIN 处理）、EOF 与 exit-status；另提供 `Exec(command) → stdout/stderr/exitCode`（用于测试连接与诊断）。
  - 验收：
    - [x] 单测覆盖读写泵的 EAGAIN、部分写、EOF
    - [ ] 集成：`stty size` 输出与请求的行列一致；resize 后再次 `stty size` 变化（等价验证已过：Windows ConPTY 实测 execWithPty 113x37 生效、shell resize 后 80x24→113x37；POSIX stty 三条用例已入测试、SSH_TEST_POSIX 门控，待 POSIX 服务器跑一次，见文末待办）
  - 验证：`pwsh scripts/verify.ps1 -Quick`

- [x] **N07 Keepalive、主动探测与重连策略** `S`
  - 依赖：N06
  - 参考：鸿蒙端 `cpp/ssh/keepalive.h`、`reconnect_policy.h`、`cpp/tests/keepalive_test.cpp`、`reconnect_policy_test.cpp`；鸿蒙端 `docs/DESIGN.md §7.5` probeNow 说明
  - 产出：`native/core/ssh/{keepalive.h,reconnect_policy.h}`、对应测试
  - 要点：`libssh2_keepalive_config` + 连续 3 个周期无入站判静默（404）；`ProbeNow()`：强发一拍并开 5 s 判定窗口；重连退避表 1/2/5/10/20/30 s，最大次数可配。
  - 验收：
    - [x] 移植用例全部通过（含 KeepaliveProbe 5 条）
  - 验证：`pwsh scripts/verify.ps1 -Quick`

- [x] **N08 统一错误码与对拍脚本** `S`
  - 依赖：N07、X04
  - 参考：鸿蒙端 `cpp/ssh/error_codes.h`、`cpp/tests/error_codes_test.cpp`；`01-DESIGN.md §6.3`
  - 产出：`native/core/ssh/error_codes.h`、`native/tests/error_codes_test.cpp`、`scripts/check-error-codes.ps1`（正式实现）
  - 要点：`kSshErrorCode*` 常量与 C# 枚举数值一致；libssh2 错误码 → 本码表映射函数；脚本解析 C# 枚举与 C++ 常量逐项比对，不一致列出差异并返回非 0。
  - 验收：
    - [x] 映射单测覆盖 libssh2 常见错误（超时、认证失败、主机密钥、socket）
    - [x] 对拍脚本在 verify 中启用且通过；故意改一个值时失败（已实测：404→440 注入报「数值不一致: KeepaliveTimeout」并退出 1）
  - 验证：`pwsh scripts/verify.ps1 -Quick`（错误码对拍为全量 verify 步骤③）

- [x] **N09a WinRT 桥：SshSession 原生实现** `M`
  - 依赖：N08
  - 参考：`01-DESIGN.md §4.2、§6.2`；鸿蒙端 `cpp/bridge/session_bridge.*`、`handle_table.h`
  - 产出：`src/SshTool.Native/Bridge/{SshSession,ConnectOptions,HostKeyInfo,HostKeyCheckEventArgs,AuthPromptEventArgs,StateChangedEventArgs}.{h,cpp}`
  - 要点：
    1. 每个 `SshSession` 拥有一条 `SessionThread`；异步方法用 `concurrency::create_async` 返回 `IAsyncOperation<int>`（错误码）。
    2. `HostKeyCheck`/`AuthPrompt`：在 I/O 线程触发事件后等待 `Accept/Reject`、`Respond/Cancel`（`std::promise` + 60/120 s 超时）；支持 `GetDeferral`。
    3. `ContentDirty` 合并投递（原子标志：已投递未消费则不再投递，C# 拉取后复位）。
    4. 生命周期：`Close()` 幂等；析构安全停止线程；I/O 线程持弱引用避免循环引用；所有事件在 I/O 线程触发（C# 负责封送）。
    5. 暴露 `ExecAsync(command)` 返回 `ExecResult{ExitCode, Stdout, Stderr}`。
  - 验收：
    - [x] ARM/x86/x64 构建通过
    - [x] 纯逻辑部分（合并投递标志、deferral 超时）抽到 `native/core/bridge_logic/` 并有单测
  - 验证：`pwsh scripts/verify.ps1 -Arm`

- [x] **N09b C# 会话抽象与原生适配** `M`
  - 依赖：N09a、X07
  - 参考：`01-DESIGN.md §4.1`（Core 不引用 Native）
  - 产出：`src/SshTool.Core/Sessions/{ISshSession.cs,ISshSessionFactory.cs,SshConnectRequest.cs,HostKeyCheck.cs,AuthPrompt.cs,SessionStateKind.cs}`、`src/SshTool.Core/Terminal/ITerminalScreen.cs`（先定义，T03 实现）、`src/SshTool.App/Platform/{NativeSshSession.cs,NativeSshSessionFactory.cs}`、`tests/SshTool.Core.Tests/Fakes/FakeSshSession.cs`
  - 要点：接口方法与事件镜像 §6.2，但只用 Core 自有类型（`Task<SshErrorCode>`、`byte[]`）；适配器负责 WinRT 类型转换与 `IAsyncOperation`→`Task`；`FakeSshSession` 可脚本化（预设每步结果、手动触发事件），供后续 SessionManager 测试。
  - 验收：
    - [x] FakeSshSession 自身单测（脚本化行为可预期）
    - [x] x64/ARM 构建通过
  - 验证：`pwsh scripts/verify.ps1`

- [x] **N10 调试连接页（M1 出口演示）** `S` 📱
  - 依赖：N09b
  - 参考：`02-UI-DESIGN.md §5.19`
  - 产出：`src/SshTool.App/Views/Debug/DebugConnectPage.xaml(.cs)`
  - 要点：输入主机/端口/用户/密码 → 连接（HostKeyCheck 弹出指纹后自动接受）→ 密码认证 → `ExecAsync("uname -a; whoami")` 显示结果；事件日志列表显示状态变化与耗时；Debug 构建主页入口。
  - 验收：
    - [ ] x64 Debug 连接 WSL sshd 成功
    - [ ] 📱 Lumia 连接局域网服务器成功
  - 验证：真机

---

## 4. M2 — 终端引擎、渲染与输入

- [x] **T01 libvterm 封装与单元格网格** `M`
  - 依赖：N01
  - 参考：鸿蒙端 `cpp/term/vterm_screen.*`、`grid.*`、`cpp/tests/vterm_screen_test.cpp`、`term_grid_test.cpp`；`01-DESIGN.md §7.1`
  - 产出：`native/core/term/{vterm_screen,grid}.{h,cpp}`、对应测试
  - 要点：16 字节单元格；默认前景/背景用标记值 `0x00000001`/`0x00000002`；属性位含 bit8 invisible、bit9「本行软换行续接」（写在行末格）；脏行位图 + `revision`；模式追踪（alt screen、DECCKM、bracketed paste、鼠标模式 1000/1002/1003、SGR 1006）；标题与 bell 回调；resize 保留内容。
  - 验收：
    - [x] 移植的 VT 语料用例全部通过（SGR/光标/滚动区/alt-screen/DECSET）
    - [x] 新增用例：默认色标记、软换行标记、模式位、宽字符续格
  - 验证：`pwsh scripts/verify.ps1 -Quick`

- [x] **T02 回滚缓冲** `S`
  - 依赖：T01
  - 参考：鸿蒙端 `cpp/term/scrollback.*`、`cpp/tests/scrollback_test.cpp`（含回滚窗口 TOCTOU 修复 `a5d759b` 的用例）
  - 产出：`native/core/term/scrollback.{h,cpp}`、对应测试
  - 要点：环形缓冲，容量 1000–50000 可配；按「距底部偏移 + 行数」取窗口；列宽变化时安全处理旧行。
  - 验收：
    - [x] 移植用例通过；写入 100000 行后内存不增长（容量封顶）
  - 验证：`pwsh scripts/verify.ps1 -Quick`

- [x] **T03 TerminalScreen 桥与会话数据接线** `M`
  - 依赖：T02、N09b
  - 参考：`01-DESIGN.md §4.3、§6.2`；鸿蒙端 `cpp/bridge/terminal_bridge.*`、`data_aggregator.*`
  - 产出：`native/core/term/snapshot.{h,cpp}`（纯函数 `CopyDirtyRows(grid, out, bitmap)`、`CopyViewport`）、`src/SshTool.Native/Bridge/TerminalScreen.{h,cpp}`、`src/SshTool.App/Platform/NativeTerminalScreen.cs`、测试 `native/tests/snapshot_test.cpp`
  - 要点：I/O 线程读到通道数据 → 喂 vterm → 触发 ContentDirty；`CopyDirtyRows` 持锁拷贝并清零脏位图；`GetText` 支持回滚区与软换行拼接；`Resize` 同步本地网格与远端 pty。
  - 验收：
    - [x] snapshot 单测：无变化返回 false；局部脏行只拷贝这些行；缓冲区大小不匹配时安全失败
    - [x] DebugConnectPage 改为开 shell 后用 `GetText` 打印屏幕文本（临时验证）
  - 验证：`pwsh scripts/verify.ps1`

- [x] **T04 全局帧调度器** `S`
  - 依赖：T03
  - 参考：`01-DESIGN.md §7.2、D14`；鸿蒙端 `view/terminal/FrameSchedulerCore.ets`
  - 产出：`src/SshTool.Core/Terminal/FrameSchedulerCore.cs`、`src/SshTool.App/Terminal/FrameScheduler.cs`、测试
  - 要点：可见集合、Wake、空闲 30 帧退订、光标闪烁相位（530 ms）；App 层订阅/退订 `CompositionTarget.Rendering`，`Wake` 可从任意线程调用。
  - 验收：
    - [x] Core 单测：空闲退订、Wake 重订阅、不可见视图不拉取、闪烁翻转时机（注入时钟）
  - 验证：`pwsh scripts/verify.ps1`

- [x] **T05 TerminalView 与基础绘制** `M`
  - 依赖：T04、SP04
  - 参考：`01-DESIGN.md §7.3`
  - 产出：`src/SshTool.Core/Terminal/{TerminalCell.cs,CellBufferReader.cs,RowRunBuilder.cs}`、`src/SshTool.App/Terminal/{TerminalView.xaml(.cs),TerminalRenderer.cs}`、测试
  - 要点：`CellBufferReader` 按小端解析 16 字节格；`RowRunBuilder` 把一行合并为 (起始列, 长度, 前景, 背景, 属性) 段（宽字符单独成段）；渲染器行缓存 `CanvasRenderTarget` + 脏行重绘 + 块光标；DebugConnectPage 换成 TerminalView（键盘输入暂用一个普通 TextBox + 发送按钮）。
  - 验收：
    - [x] Core 单测：解析、run 合并边界（属性变化/宽字符/行尾空白）
    - [ ] x64 Debug：连接后 `ls --color` 彩色显示正确
    - [ ] 📱 Lumia 上显示正确
  - 验证：`pwsh scripts/verify.ps1`；真机

- [x] **T06 渲染属性、宽字符、调色板与设备丢失** `M`
  - 依赖：T05
  - 参考：`01-DESIGN.md §7.1、§7.3`
  - 产出：`src/SshTool.Core/Terminal/TerminalPalette.cs`、渲染器更新、测试
  - 要点：bold（粗体字重或描边）、bold-as-bright（ANSI 0–7 映射 8–15）、italic、underline、strike、dim、reverse、invisible；默认色标记解析为外观前景/背景；宽字符 2 格定位居中；光标样式 block/bar/underline + 闪烁（受调度器相位）；失焦时空心光标；`DeviceLost` 重建全部资源。
  - 验收：
    - [x] Palette 单测：标记值、bold-as-bright、reverse 交换、dim alpha
    - [ ] x64：`printf` 测试脚本（放 `tools/term-test/attrs.sh`）各属性显示正确截图记入进度日志
    - [ ] 📱 中文与 emoji 对齐正确
  - 验证：`pwsh scripts/verify.ps1`；真机

- [x] **T07 字体度量与网格尺寸** `S`
  - 依赖：T06
  - 参考：`01-DESIGN.md §7.4`
  - 产出：`src/SshTool.Core/Terminal/GridSizeCalculator.cs`、`src/SshTool.App/Terminal/FontMetrics.cs`、测试
  - 要点：测 cellW/cellH；`cols/rows` 计算（padding、键条覆盖、最小 20×5）；尺寸变化防抖 100 ms 调 `Resize`；字号/DPI 变化重测。
  - 验收：
    - [x] Calculator 单测（多组宽高/字号/padding）
    - [ ] 📱 旋转屏幕后 `stty size` 与显示一致
  - 验证：`pwsh scripts/verify.ps1`；真机

- [x] **T08 键位映射与粘滞修饰键** `M`
  - 依赖：X02
  - 参考：`01-DESIGN.md §7.5`；鸿蒙端 `view/terminal/KeyMap.ets`、`StickyModifiers.ets` 及其测试
  - 产出：`src/SshTool.Core/Terminal/{KeyChord.cs,TerminalKey.cs,TerminalModes.cs,KeyMap.cs,StickyModifiers.cs}`、测试
  - 要点：§7.5 映射表全部实现；Backspace 可配 DEL/BS；StickyModifiers：点按一次=单次、双击或长按=锁定、再点=释放、使用后单次自动释放。
  - 验收：
    - [x] KeyMap 单测逐行覆盖映射表（普通/应用光标模式/修饰组合/F1–F12/Ctrl 特殊字符）
    - [x] StickyModifiers 状态机单测 ≥8 条
  - 验证：`dotnet test`

- [x] **T09 软键盘输入通路** `M`
  - 依赖：T07、T08、SP05
  - 参考：`01-DESIGN.md §7.5`（以 SP05 回写后的版本为准）
  - 产出：`src/SshTool.Core/Terminal/SentinelDiff.cs`、`src/SshTool.App/Terminal/SoftKeyboardInput.cs`、TerminalView 集成、测试
  - 要点：哨兵差分纯函数（新增文本 / 删除个数）；组合态处理；回车；InputPane 遮挡 → 终端可视区收缩并 resize；点击终端弹键盘；TerminalView 暴露 `Input` 事件（bytes）。
  - 验收：
    - [x] SentinelDiff 单测（新增、删除、替换、多字符提交、哨兵被整体删除后恢复）
    - [ ] 📱 英文、退格、回车、中文拼音「你好」均正确送达远端
  - 验证：`pwsh scripts/verify.ps1`；真机

- [x] **T10 功能键条** `M`
  - 依赖：T09
  - 参考：`02-UI-DESIGN.md §5.6`；鸿蒙端 `view/KeyBar.ets`
  - 产出：`src/SshTool.Core/Terminal/KeyBarLayout.cs`、`src/SshTool.App/Controls/KeyBar.xaml(.cs)`、`src/SshTool.App/Platform/Haptics.cs`、测试
  - 要点：布局字符串解析/序列化（未知 id 忽略、去重）；横向滚动；修饰键三态视觉；方向键长按连发；`paste`/`copy`/`hidekb`/`snippets` 发出动作事件；触感（`Windows.Phone.Devices.Notification.VibrationDevice`，ApiInformation 守卫）。
  - 验收：
    - [x] KeyBarLayout 单测
    - [ ] 📱 单手完成 Ctrl+C、Ctrl+Z、Esc、Tab 补全；锁定 Ctrl 连续发送多个控制字符
  - 验证：`pwsh scripts/verify.ps1`；真机

- [x] **T11 物理键盘与快捷键表** `S`
  - 依赖：T09
  - 参考：`01-DESIGN.md §7.5`；`02-UI-DESIGN.md §5.16`
  - 产出：`src/SshTool.Core/Terminal/ShortcutMap.cs`、`src/SshTool.App/Terminal/HardwareKeyboardInput.cs`、测试
  - 要点：`CoreWindow.KeyDown/CharacterReceived` + `AcceleratorKeyActivated`（Alt 组合）；先匹配快捷键（动作枚举）再走 KeyMap；有物理键盘输入时隐藏 SIP 并把焦点留在 TerminalView；快捷键表 JSON 序列化与默认值。
  - 验收：
    - [x] ShortcutMap 单测：默认表、覆盖、冲突检测、序列化往返
    - [ ] 📱（蓝牙键盘或 Continuum）Ctrl+C、Alt+B、方向、F1–F12 正确
  - 验证：`pwsh scripts/verify.ps1`；真机

- [x] **T12 触摸选择与复制** `M`
  - 依赖：T06
  - 参考：`01-DESIGN.md §7.6`；`02-UI-DESIGN.md §5.5`；鸿蒙端 `view/terminal/SelectionModel.ets`
  - 产出：`src/SshTool.Core/Terminal/SelectionModel.cs`、`src/SshTool.App/Terminal/SelectionLayer.xaml(.cs)`、`src/SshTool.App/Platform/ClipboardService.cs`、测试
  - 要点：绝对行坐标；选词（字母数字与 `-_./~` 视为词内）、选行；宽字符边界扩展；拖柄；浮动工具条（复制/粘贴/全选/分享）；复制文本走 `GetText`（软换行不插换行，行尾空白裁剪）；TransientToast + 触感。
  - 验收：
    - [x] SelectionModel 单测 ≥10 条（跨行、宽字符、反向拖动、回滚区）
    - [ ] 📱 长按选词、拖柄扩展、复制到系统剪贴板
  - 验证：`pwsh scripts/verify.ps1`；真机

- [x] **T13 粘贴与多行确认** `S`
  - 依赖：T12、T10
  - 参考：`01-DESIGN.md §7.7`
  - 产出：`src/SshTool.Core/Terminal/PasteProcessor.cs`、`src/SshTool.App/Dialogs/PasteConfirmDialog.xaml(.cs)`、测试
  - 要点：换行归一为 `\r`；bracketed paste 包裹；4 KB 分块；多行确认（设置 `pasteConfirmMultiline`，对话框「不再提示」写回设置——设置仓库在 D04，此处先用接口 + 内存实现）。
  - 验收：
    - [x] PasteProcessor 单测（CRLF/LF/CR 混合、包裹、分块边界不切断 UTF-8 多字节字符）
  - 验证：`pwsh scripts/verify.ps1`

- [x] **T14 回滚滚动与捏合缩放** `M`
  - 依赖：T06
  - 参考：`01-DESIGN.md §7.6`
  - 产出：`src/SshTool.Core/Terminal/{ScrollController.cs,MouseEncoder.cs}`（滚轮部分）、`src/SshTool.App/Terminal/PointerInput.cs`（触摸部分）、测试
  - 要点：视口偏移（0 = 底部），`CopyViewport` 渲染；惯性（ManipulationInertiaStarting 减速度）；alt-screen 下拖动转方向键或滚轮序列（设置 `altScreenScroll`）；有新输入或用户输入时回到底部；捏合改字号（8–28）并显示气泡，松手回调保存。
  - 验收：
    - [x] ScrollController 单测（边界夹取、新输出时保持/回底策略）
    - [ ] 📱 普通屏回滚流畅；`less` 中滑动翻页；捏合不抖
  - 验证：`pwsh scripts/verify.ps1`；真机

- [x] **T15 鼠标、触控板与终端鼠标上报** `M`
  - 依赖：T12、T14
  - 参考：`01-DESIGN.md §7.6`
  - 产出：`src/SshTool.Core/Terminal/MouseEncoder.cs`（完整）、`PointerInput.cs` 鼠标部分、右键 MenuFlyout、测试
  - 要点：X10/普通/按钮事件/任意移动 四种模式 × 默认与 SGR 编码；Shift+拖动强制本地选择；双击选词、三击选行；滚轮；右键菜单（复制/粘贴/全选/清屏）；鼠标悬停 IBeam 光标。
  - 验收：
    - [x] MouseEncoder 单测覆盖各模式与坐标 >223 时 SGR 编码
    - [ ] 📱（Continuum）vim `:set mouse=a` 点击定位、htop 点击、tmux 滚轮正常
  - 验证：`pwsh scripts/verify.ps1`；真机

---

## 5. M3 — 数据层与主机管理

- [x] **D01 核心模型与校验** `M`
  - 依赖：X02
  - 参考：`01-DESIGN.md §8.1`；`03-SYNC-PROTOCOL.md §4.1`（限制值）
  - 产出：`src/SshTool.Core/Models/{Host,HostGroup,Tunnel,TunnelType,AuthType,KeyEntry,Snippet,AppearanceProfile,KnownHost,CursorStyle}.cs`、`src/SshTool.Core/Models/Defaults.cs`、`src/SshTool.Core/Validation/{ValidationResult,HostValidator,GroupValidator,TunnelValidator,SnippetValidator}.cs`、`src/SshTool.Core/Common/IdGenerator.cs`、测试
  - 要点：全部字段按 §8.1；每个实体 `Clone()`；默认值工厂；校验返回「字段名 → 错误资源键」字典；Host：name/host/username 非空与长度、port、keepalive 范围；Group：color 正则；Tunnel：按类型的必填（dynamic 不需要 dest，relay 需要 destServerId）、端口范围；`IdGenerator.NewId()` 生成小写 UUID v4。
  - 验收：
    - [x] 每条约束至少一条通过 + 一条失败用例
    - [x] Clone 深拷贝（列表/字典不共享）
  - 验证：`dotnet test`

- [x] **D02 JSON 仓库、编解码与迁移** `M`
  - 依赖：D01、X04
  - 参考：`01-DESIGN.md §8.2、§8.4、D8、D9`
  - 产出：`src/SshTool.Core/Storage/{IFileSystem,InMemoryFileSystem,JsonStore,IEntityCodec,ChangeOrigin,RepositoryChangedEventArgs,Repository,ConfigService}.cs`、`Storage/Codecs/*.cs`（7 个实体）、`Storage/Repositories/{HostRepository,GroupRepository,TunnelRepository,SnippetRepository,AppearanceRepository,KnownHostRepository,KeyRepository}.cs`、`src/SshTool.App/Platform/UwpFileSystem.cs`、测试
  - 要点：
    1. 文件格式 `{ "schemaVersion": 1, "items": [...] }`；手写 JObject 编解码（不用反射）；未知字段保留在实体的 `Extra`（JObject）中以便未来兼容。
    2. 原子保存（`*.tmp` → 替换）；损坏文件备份为 `*.corrupt-yyyyMMddHHmmss` 并以空集合继续 + 对外暴露 `LoadWarnings`。
    3. 迁移：`IMigration { From, To, Apply(JObject) }` 链式执行（放一个 no-op v1 示例与测试）。
    4. 仓库：内存缓存、串行化写入（`SemaphoreSlim`）、`Changed` 事件带 `ChangeOrigin`（User/Sync）与变更实体 id 列表；`ReplaceAll(items, origin)` 供同步使用。
    5. `ConfigService` 协调多仓库引用规则：删主机 → 删其隧道（含 relay `destServerId` 指向者）、清其他主机 `jumpHostId`、调用 `ISecretStore` 级联（D03 前注入空实现）；删分组 → 清主机 `groupId` 与隧道 `groupId`；删密钥 → 被主机引用时拒绝。
  - 验收：
    - [x] 7 个编解码器往返单测
    - [x] JsonStore：原子写（模拟写一半失败原文件不变）、损坏文件备份、迁移链
    - [x] ConfigService 引用规则单测
  - 验证：`dotnet test`

- [x] **D03 凭据安全存储** `S`
  - 依赖：D02、SP06
  - 参考：`01-DESIGN.md §8.2、D10`
  - 产出：`src/SshTool.Core/Storage/{InMemorySecretStore,ISecureFile,InMemorySecureFile}.cs`（`ISecretStore`/`SecretKeys` 已随 D02 落地）、`src/SshTool.App/Platform/{DpapiSecureFile,DpapiSecretStore}.cs`、测试
  - 要点：`SecretKeys.HostPassword(id)` 等构造函数（键名规范 §8.2）；`GetAsync/SetAsync/RemoveAsync/RemoveByPrefixAsync`；DPAPI 实现把整个键值表 JSON 加密为 `secure/secrets.bin`（串行化访问、原子写）；`ISecureFile` 供 AuthStore/VaultCache 复用；ConfigService 级联删除接入真实接口。
  - 验收：
    - [x] InMemory 实现与键名构造单测；前缀删除
    - [ ] 📱 真机写入后用设备门户下载 LocalFolder，`secrets.bin` 中搜索不到测试密码明文
  - 验证：`dotnet test`；真机

- [x] **D04 设置仓库** `S`
  - 依赖：X02
  - 参考：`01-DESIGN.md §8.3`；鸿蒙端 `repository/SettingsRepository.ets`
  - 产出：`src/SshTool.Core/Storage/{ISettingsStore,InMemorySettingsStore,SettingDefinitions,SettingsRepository}.cs`、`src/SshTool.App/Platform/LocalSettingsStore.cs`、测试
  - 要点：定义表驱动默认值与类型；每个键一对强类型访问器；枚举类字符串读到非法值回退默认；`EnsureDefaults()`；`Changed` 事件。T13 用到的临时设置接口在此替换为正式实现（T13 尚未开始，无既有临时接口需替换）。
  - 验收：
    - [x] 单测：定义表与访问器完整性（反射只在测试里用）、非法值回退、默认值写入
  - 验证：`dotnet test`

- [x] **D05 组合根与启动流程** `S` 📱
  - 依赖：D02、D03、D04、X05、X07
  - 参考：`01-DESIGN.md §4.1`
  - 产出：`src/SshTool.App/App.xaml.cs`（组合根）、`src/SshTool.App/Infrastructure/AppServices.cs`
  - 要点：`OnLaunched`：Logger → AppConfig → Settings.EnsureDefaults → 各仓库 LoadAsync（有 LoadWarnings 时主页 Banner）→ ThemeService → 注册服务 → 导航 MainPage；`Suspending`（deferral）刷盘；未处理异常记录日志；启动各阶段耗时写日志。
  - 验收：
    - [ ] x64 Debug 冷启动进入 MainPage，日志含各阶段耗时
    - [ ] 📱 ARM Release 冷启动正常
  - 验证：msbuild；真机

- [x] **U01 MainPage 外壳与导航** `S`
  - 依赖：D05
  - 参考：`02-UI-DESIGN.md §4、§5.1`
  - 产出：`src/SshTool.App/Views/MainPage.xaml(.cs)`、`src/SshTool.App/ViewModels/MainViewModel.cs`、`src/SshTool.App/Views/PlaceholderPage.xaml(.cs)`
  - 要点：Pivot（主机/会话/隧道，内容先为占位 EmptyState）；底部 CommandBar（新建/搜索/同步图标占位/更多）；「更多」菜单项全部可导航，未实现页面导航到 `PlaceholderPage`（显示「将在 Mx 提供」）；返回键规则第 7 条；状态栏颜色随主题。
  - 验收：
    - [x] 所有菜单项可导航且可返回
    - [x] check-magic-numbers 通过
  - 验证：`pwsh scripts/verify.ps1`

- [x] **U02 主机列表** `M` 📱
  - 依赖：U01
  - 参考：`02-UI-DESIGN.md §5.1`；鸿蒙端 `viewmodel/HostListViewModel.ets`（纯函数与用例）
  - 产出：`src/SshTool.Core/Hosts/{HostListBuilder,HostListRow,QuickConnectParser,IHostStatusProvider}.cs`、`src/SshTool.App/ViewModels/HostListViewModel.cs`、`src/SshTool.App/Views/Main/HostsPivot.xaml(.cs)`、`src/SshTool.App/Controls/HostRow.xaml(.cs)`、测试
  - 要点：分组/排序/搜索（名称、地址、用户，大小写不敏感）纯函数；分组折叠持久化到 `hostGroupCollapsed`；徽标；状态点从 `IHostStatusProvider` 取（先用空实现）；快速连接解析 `user@host[:port]`（含 IPv6 `[::1]:22`）；空状态与无结果状态；长按/右键 MenuFlyout；删除确认（显示将级联删除的隧道数）；Debug 菜单「生成 100 台测试主机」。
  - 验收：
    - [x] Core 单测 ≥20 条（分组、两种排序、搜索、折叠、快速连接解析含非法输入）
    - [ ] 📱 100 台主机滚动流畅
  - 验证：`pwsh scripts/verify.ps1`；真机

- [x] **U03a 主机编辑页：表单、校验与保存** `M`
  - 依赖：U02
  - 参考：`02-UI-DESIGN.md §5.4`；鸿蒙端 `viewmodel/HostEditViewModel.ets`
  - 产出：`src/SshTool.App/ViewModels/HostEditViewModel.cs`、`src/SshTool.App/Views/HostEditPage.xaml(.cs)`、`src/SshTool.Core/Hosts/{HostEditState,JumpChainValidator}.cs`、测试
  - 要点：Pivot 连接/认证（占位）/终端/高级；字段绑定与实时校验（D01 校验器）；「仅本机」标注；环境变量行编辑器；初始命令多行；跳板主机下拉排除自己与成环项；新建/编辑/复制为新主机三种模式；脏检查 + 返回确认；保存以 `ChangeOrigin.User` 写入。
  - 验收：
    - [x] Core 单测：HostEditState 脏检查、JumpChainValidator（自环、多级成环、最大深度 5）
    - [x] 保存校验失败时跳到对应 Pivot 并聚焦字段
  - 验证：`pwsh scripts/verify.ps1`

- [x] **U03b 主机编辑页：认证与凭据** `M`
  - 依赖：U03a、D03
  - 参考：`02-UI-DESIGN.md §5.4`；`01-DESIGN.md §12.1`
  - 产出：HostEditPage 认证 Pivot、`HostEditViewModel` 凭据部分、`src/SshTool.Core/Hosts/CredentialDraft.cs`、测试
  - 要点：认证方式单选；密码 + 「保存在本机」；私钥下拉（KeyRepository，显示名称/类型/指纹尾 8 位），[导入]/[生成] 在 K02 前禁用并提示；短语 + 保存；保存时写 SecretStore，取消保存则删除已存值；保存后立即清空明文字段；切换认证方式时清理不再适用的凭据（确认）。
  - 验收：
    - [x] CredentialDraft 单测：勾选/取消保存、切换认证方式的清理规则
    - [x] 编辑已保存密码的主机时密码框显示占位「已保存」，不回显明文
  - 验证：`pwsh scripts/verify.ps1`

- [x] **U04 分组管理** `S`
  - 依赖：U02
  - 参考：`02-UI-DESIGN.md §5.1`（分组头长按）
  - 产出：`src/SshTool.App/Views/GroupManagePage.xaml(.cs)`、`src/SshTool.App/ViewModels/GroupManageViewModel.cs`、`src/SshTool.App/Controls/ColorSwatchPicker.xaml(.cs)`（基础版：24 预设色 + Hex 输入）
  - 要点：新建（默认色 `#4F8CFF`）、重命名、改色、上移/下移（order）、删除（提示受影响主机与隧道数）；HostEdit 分组下拉旁 [管理分组] 入口。
  - 验收：
    - [x] 删除分组后主机 groupId 为空、隧道 groupId 为 null（仓库层已单测，UI 手测记录到进度日志）
  - 验证：`pwsh scripts/verify.ps1`

- [x] **U05 连接相关对话框组件** `M`
  - 依赖：X07
  - 参考：`02-UI-DESIGN.md §5.8`
  - 产出：`src/SshTool.App/Dialogs/{HostKeyDialog,HostKeyMismatchDialog,CredentialDialog,PassphraseDialog,KbdInteractiveDialog,ConfirmDialog,ExitWithSessionsDialog}.xaml(.cs)`、`src/SshTool.App/Controls/{RandomArtView,MonoText}.xaml(.cs)`、画廊页追加演示
  - 要点：每个对话框提供 `static Task<TResult> ShowAsync(...)` 并经 DialogService 排队；结果类型化（如 `CredentialDialogResult { Password, Remember, Cancelled }`）；返回键 = 取消；PasswordBox 取值后清空控件。
  - 验收：
    - [ ] 画廊页可逐个弹出并返回正确结果
    - [ ] 连续请求两个对话框时按顺序显示不崩溃
  - 验证：`pwsh scripts/verify.ps1`

- [x] **U06 已知主机页** `S`
  - 依赖：D02、U05
  - 参考：`02-UI-DESIGN.md §5.11`
  - 产出：`src/SshTool.App/Views/KnownHostsPage.xaml(.cs)`、`src/SshTool.App/ViewModels/KnownHostsViewModel.cs`
  - 要点：列表、搜索、详情（完整指纹 + randomart）、删除确认。
  - 验收：
    - [x] 列表/搜索/删除可用；「删除后再次连接会重新弹 TOFU」在 D06 后回归（抄到真机验收待办）
  - 验证：`pwsh scripts/verify.ps1`

---

## 6. M4 — 终端页与会话

- [x] **D06 SessionManager** `M`
  - 依赖：D02、D03、N09b、T03
  - 参考：`01-DESIGN.md §9`；鸿蒙端 `service/SessionManager.ets` 与 `SessionManager.test.ets`
  - 产出：`src/SshTool.Core/Sessions/{SessionInfo,SessionManager,IHostKeyPrompter,ICredentialPrompter,ITimerFactory,IUiDispatcher,ReconnectScheduler,HostKeyVerifier}.cs`、测试
  - 要点：
    1. `OpenAsync(hostId | quickConnectTarget, gridSize)` 按 §9.2 编排：HostKeyVerifier（KnownHost 优先、同步指纹次之、未知走 prompter）、凭据解析（SecretStore → prompter → 按需记住）、认证失败重试 ≤3、KI 转 prompter、开 shell、成功后写 hostFingerprint（原为空时）与 lastConnectedAt。
    2. 重连：退避调度、每秒更新 `ReconnectInSeconds`、不自动重连的错误集合（2xx、303）、`ReconnectNow`/`CancelReconnect`；替换原生会话时旧会话事件按实例比对丢弃。
    3. `Close(sessionId)`、`CloseAll()`、`ActiveSessionCount`、`SessionsChanged` 事件、`IHostStatusProvider` 实现（替换 U02 空实现）。
    4. 属性变更经注入的 `IUiDispatcher`（测试用同步实现）。
  - 验收：
    - [x] 单测 ≥15 条：首次连接 TOFU 接受/拒绝、指纹不匹配拒绝、同步指纹匹配自动写 KnownHost、无密码弹框并记住、密码错误重试 3 次、KI、断线自动重连成功、认证错误不重连、用户关闭停止重连、旧句柄迟到事件被忽略、CloseAll
  - 验证：`dotnet test`

- [x] **D07 测试连接服务** `S`
  - 依赖：D06、U03b
  - 参考：`02-UI-DESIGN.md §5.4`；鸿蒙端 `service/TestConnection.ets`
  - 产出：`src/SshTool.Core/Sessions/ConnectionTester.cs`、HostEditPage [测试连接] 接线、测试
  - 要点：使用编辑中的草稿（未保存）；阶段进度（解析/握手/主机密钥/认证/完成）；15 s 总超时；不开 shell；信任主机密钥时写 KnownHost（幂等）；凭据不落盘除非用户勾选保存。
  - 验收：
    - [x] 单测：各阶段失败的结果文案键、超时、取消
  - 验证：`pwsh scripts/verify.ps1`

- [x] **U07 终端页（单窗格）** `M` 📱
  - 依赖：D06、T10、T11、T13、T14、U05
  - 参考：`02-UI-DESIGN.md §5.5`；鸿蒙端 `pages/TerminalPage.ets`
  - 产出：`src/SshTool.App/Views/TerminalPage.xaml(.cs)`、`src/SshTool.App/ViewModels/TerminalViewModel.cs`、`src/SshTool.App/Platform/{UwpHostKeyPrompter,UwpCredentialPrompter,StatusBarService}.cs`
  - 要点：导航参数（sessionId 或 hostId）；信息条（可收起）；菜单动作；状态栏隐藏/恢复；方向与 InputPane 变化 → resize；主机列表点击 → `SessionManager.OpenAsync` 并导航；已连接会话直接切换；离开页面不断开；FrameScheduler 可见性随导航更新。
  - 验收：
    - [ ] x64：从主机列表到出现提示符，vim/htop 可用
    - [ ] 📱 同上，且无白屏跳变；旋转屏幕后布局与 `stty size` 正确
  - 验证：`pwsh scripts/verify.ps1`；真机

- [x] **U08 连接蒙层与重连交互** `S` 📱
  - 依赖：U07
  - 参考：`02-UI-DESIGN.md §5.5` 蒙层表；鸿蒙端 `viewmodel/TerminalViewModel.ets` 的 `deriveOverlay`
  - 产出：`src/SshTool.Core/Sessions/OverlayStateDeriver.cs`、`src/SshTool.App/Controls/SessionOverlay.xaml(.cs)`、测试
  - 要点：SessionInfo → OverlayKind（None/Connecting/Reconnecting/Error/Closed/PolicyDisconnected）+ 文案键 + 可用按钮；淡入淡出 150 ms；按钮接 SessionManager；错误文案 `Error_<code>`。
  - 验收：
    - [x] Deriver 单测覆盖全部状态组合
    - [ ] 📱 断网后看到倒计时，「立即重连」可用
  - 验证：`pwsh scripts/verify.ps1`；真机

- [x] **U09 会话列表、侧栏与会话恢复** `S`
  - 依赖：U07
  - 参考：`02-UI-DESIGN.md §5.2、§5.5`；`01-DESIGN.md §10`
  - 产出：`src/SshTool.App/Views/Main/SessionsPivot.xaml(.cs)`、`src/SshTool.App/Controls/SessionsPane.xaml(.cs)`、`src/SshTool.App/ViewModels/SessionsPaneViewModel.cs`、`src/SshTool.Core/Sessions/SessionSnapshotStore.cs`、测试
  - 要点：会话 Pivot 与终端页 SplitView 侧栏共用 ViewModel；关闭/切换/新建；Suspending 时写 `state/sessions.json`（hostId 列表 + 窗格布局）；冷启动显示「上次未关闭的会话」卡片与 [全部恢复]；有活跃会话时退出应用确认。
  - 验收：
    - [x] SnapshotStore 单测（往返、主机已删除时过滤）
  - 验证：`pwsh scripts/verify.ps1`

- [x] **U10 连接后自动执行：tmux 附着、初始命令、环境变量** `S` 📱
  - 依赖：D06
  - 参考：`01-DESIGN.md §9.4`；鸿蒙端 `common/utils/AutoRun.ets`
  - 产出：`src/SshTool.Core/Sessions/AutoRun.cs`、SessionManager 接线、测试
  - 要点：tmux 会话名只允许 `[A-Za-z0-9_.-]`，其他字符替换为 `_`；空名 `main`；每次（含重连）shell 打开后发送；环境变量经连接选项 `Env` 传 native；日志只记条数。
  - 验收：
    - [x] AutoRun 单测（顺序、转义、空行跳过、全部关闭时为空）
    - [ ] 📱 开启 tmux 的主机断网重连后回到原 tmux 现场
  - 验证：`dotnet test`；真机

- [x] **U11 窗格树模型** `M`
  - 依赖：X02
  - 参考：鸿蒙端 `viewmodel/PaneTree.ets` 与其测试（16 条）；鸿蒙端 `docs/DESIGN.md §4.3.1`
  - 产出：`src/SshTool.Core/Terminal/{PaneTree,PaneNode,TabSet,PaneLayout}.cs`、测试
  - 要点：Tab → 二叉树；Leaf 绑定 sessionId；Split(row/column, ratio 夹在 0.15–0.85)；关闭叶子兄弟上提；布局计算（容器矩形 → 叶子矩形与分隔条矩形）；按方向查找相邻叶子；序列化（供会话恢复）。
  - 验收：
    - [x] 移植 16 条用例 + 布局计算与方向导航 ≥6 条（鸿蒙端仓库不在本机：按要点等价覆盖 16 条结构用例 + 10 条布局/导航）
  - 验证：`dotnet test`

- [x] **U12 宽屏：主从布局、多标签与分屏** `M` 📱
  - 依赖：U11、U09、T15
  - 参考：`02-UI-DESIGN.md §3、§5.7、§5.16`
  - 产出：`src/SshTool.App/Controls/{TerminalWorkspace,TabStrip}.xaml(.cs)`、MainPage/TerminalPage 自适应状态、`src/SshTool.App/ViewModels/WorkspaceViewModel.cs`
  - 要点：`AdaptiveTrigger ≥720`：MainPage 左主机列表 + 右 TerminalWorkspace；标签栏（新建/关闭/右键菜单）；按 PaneLayout 绝对定位 TerminalView 与分隔条（拖动改 ratio，释放后各窗格 resize）；焦点边框；快捷键动作接线；窄屏只显示聚焦叶子且窗格树不销毁；不可见窗格不绘制。
  - 验收：
    - [ ] x64 大窗口：4 窗格同时输出互不串扰，拖分隔条后每个窗格 `stty size` 各自正确
    - [ ] 📱 Continuum 下同上；断开 Continuum 回到手机后会话与布局完好
  - 验证：`pwsh scripts/verify.ps1`；真机

- [x] **U13 命令片段** `M`
  - 依赖：D02、U07
  - 参考：`02-UI-DESIGN.md §5.9`
  - 产出：`src/SshTool.Core/Terminal/SnippetTemplate.cs`、`src/SshTool.App/Views/{SnippetsPage,SnippetEditPage}.xaml(.cs)`、`src/SshTool.App/Controls/SnippetPickerFlyout.xaml(.cs)`、`src/SshTool.App/Dialogs/SnippetVariableDialog.xaml(.cs)`、ViewModels、测试
  - 要点：内置变量 `${host} ${user} ${port} ${name}`，其他 `${xxx}` 视为待填变量，`$${` 转义；发送后是否回车；按分组名分组；终端菜单与键条 `snippets` 键打开选择器。
  - 验收：
    - [x] SnippetTemplate 单测（替换、未知变量收集、转义、多次出现）
  - 验证：`pwsh scripts/verify.ps1`

- [x] **P01 屏幕常亮、应用生命周期与后台策略** `M` 📱
  - 依赖：U07、SP06
  - 参考：`01-DESIGN.md §10`（以 SP06 回写后的版本为准）；鸿蒙端 `service/background/BackgroundPolicy.ets`、`service/ScreenAwake.ets` 与其测试
  - 产出：`src/SshTool.Core/Lifecycle/{KeepAwakePolicy,BackgroundPolicy}.cs`、`src/SshTool.App/Platform/{KeepAwakeService,LifecycleService}.cs`、测试
  - 要点：`KeepAwakePolicy.ShouldKeepScreenOn(mode, terminalVisible, activeSessions)`；DisplayRequest 成对调用计数保护；`BackgroundPolicy` 纯状态机：输入（进入后台、扩展执行授予/拒绝/撤销、回前台、计时到期、会话数变化）→ 输出动作（请求扩展执行、策略性断开、恢复重连、探测、释放）；接线 EnteredBackground/LeavingBackground/Suspending/Resuming；405 策略断开保留会话面孔。
  - 验收：
    - [x] KeepAwakePolicy 全组合单测；BackgroundPolicy ≥15 条
    - [ ] 📱 终端页不自动息屏；切走再回来：仍在线或自动重连
  - 验证：`dotnet test`；真机

- [x] **P02 网络变化与主动探测** `S` 📱
  - 依赖：P01
  - 参考：`01-DESIGN.md §10`；鸿蒙端 `service/NetworkWatcher.ets`
  - 产出：`src/SshTool.Core/Lifecycle/NetworkChangeDetector.cs`、`src/SshTool.App/Platform/NetworkMonitor.cs`、SessionManager `OnNetworkChanged`、省电模式 Banner、测试
  - 要点：`NetworkStatusChanged` 防抖 1 s，以「网络适配器 id + 连接级别」变化为判据；退避中的会话立即重连、已连接的 `ProbeNow`；`PowerManager.EnergySaverStatusChanged` → 终端页 Banner。
  - 验收：
    - [x] Detector 单测（同网抖动不触发、切网触发、断网→恢复触发）
    - [ ] 📱 Wi-Fi 切蜂窝后 10 s 内会话恢复可用
  - 验证：`dotnet test`；真机

- [x] **U14 设置页** `M`
  - 依赖：D04、T10、T11、P01
  - 参考：`02-UI-DESIGN.md §5.15`
  - 产出：`src/SshTool.App/Views/{SettingsPage,KeyBarLayoutEditorPage,ShortcutEditorPage}.xaml(.cs)`、`src/SshTool.App/ViewModels/SettingsViewModel.cs`
  - 要点：五个 Pivot 按表实现（外观相关项先链接到占位页，A05 补齐）；键条布局编辑（可用/已选两列 + 上下移动 + 恢复默认）；快捷键录制；日志导出（FileSavePicker 合并 `logs/*.log`）与清空；诊断信息（OS 版本由 `AnalyticsInfo.VersionInfo.DeviceFamilyVersion` 解码、`MemoryManager.AppMemoryUsageLimit`、ApiInformation 探测结果）。
  - 验收：
    - [x] 每项设置修改后立即生效（手测清单见本行进度日志 U14 条）
  - 验证：`pwsh scripts/verify.ps1`

---

## 7. M5 — 云端同步

> 开工前必读 `03-SYNC-PROTOCOL.md` 全文。单测名称尽量与其 `§10.1` 列表一致，便于对照桌面端用例。

- [x] **S01 同步常量与文档模型** `S`
  - 依赖：D01
  - 参考：`03-SYNC-PROTOCOL.md §3.1、§4.1`；桌面端 `src/shared/sync-types.ts`
  - 产出：`src/SshTool.Core/Sync/Protocol/{SyncConstants,SyncDocumentV1,PortableServerProfile,ServerSecrets,ServerRecord,TunnelRecord,GroupRecord,SyncPreferencesV1}.cs`、测试
  - 要点：常量逐项对齐；模型提供 `Clone`；`ServerSecrets` 字段为 null 表示「键不存在」。
  - 验收：
    - [x] 常量单测（写死期望值，含 AAD 域字符串与 SPM1）
  - 验证：`dotnet test`

- [x] **S02 文档严格校验与读写器** `M`
  - 依赖：S01
  - 参考：`03-SYNC-PROTOCOL.md §4`；桌面端 `src/shared/sync-schemas.ts`、`src/shared/sync-private-key.ts`、`test/sync-serializer.test.ts`
  - 产出：`src/SshTool.Core/Sync/Protocol/{SyncDocumentValidator,SyncDocumentReader,SyncDocumentWriter,SyncDocumentInvalidException,CanonicalBase64,PrivateKeyFormat}.cs`、`tests/SshTool.Core.Tests/Sync/SyncDocumentTests.cs`、`tests/fixtures/sync/*.json`
  - 要点：严格按 §4.1 校验每个约束（错误带 JSON 路径）；Reader 拒绝未知/缺失键与类型不符（整数必须是整数 token）；Writer 固定键序、序数排序、UTF-8 无 BOM、无缩进、>2 MiB 拒绝；`SameContent`（忽略 updatedAt）。
  - 验收：
    - [x] §10.1 Serializer 1–4 全部用例，§4.1 每条约束至少一条失败用例
    - [x] 同一文档两次写出字节相同
  - 验证：`dotnet test`

- [x] **S03 原生保险库密码学** `M`
  - 依赖：N01
  - 参考：`03-SYNC-PROTOCOL.md §3`；鸿蒙端 `cpp/crypto/{sync_params.h,aad.*,vault.*}`、`cpp/tests/{aad_test,vault_test,vault_ops_test}.cpp`、`vault_golden_vectors.h`；桌面端 `src/main/security/crypto-vault.ts`
  - 产出：`native/core/crypto/{sync_params.h,aad.hpp,aad.cpp,vault.hpp,vault.cpp}`、`native/tests/{aad_test,vault_test,vault_ops_test}.cpp`、`native/tests/vault_golden_vectors.h`（原样复制）
  - 要点：KDF 参数作为入参并做范围校验；解包/解密失败统一返回 false；解密前常量时间比对 ciphertextHash；Base64 规范性检查；恢复密钥校验段大小写不敏感比较；所有密钥材料清零。
  - 验收：
    - [x] 黄金向量通过；恢复密钥 10000 次随机往返与逐字符篡改被拒
    - [x] 篡改 ciphertext / hash / AAD 字段（vaultId、keyVersion）均解密失败
    - [x] 非法 KDF 参数被拒
  - 验证：`pwsh scripts/verify.ps1 -Quick`

- [x] **S04 跨端测试向量工具** `M`
  - 依赖：S03、S02
  - 参考：`03-SYNC-PROTOCOL.md §3.5、§10.1 Serializer 5`
  - 产出：`tools/sync-vectors/{package.json,generate.mjs,validate-fixtures.mjs,README.md}`、`native/tests/desktop_vectors.h`、`tests/fixtures/sync/desktop-vectors.json`、`tests/fixtures/sync/desktop-document-*.json`、`native/tests/desktop_vectors_test.cpp`、`tests/SshTool.Core.Tests/Sync/DesktopFixtureTests.cs`、`scripts/verify.ps1` 增加 `-Interop`
  - 要点：
    1. `generate.mjs`：`hash-wasm` 版本与桌面端 `package-lock.json` 一致；固定 salt/nonce/同步密码/恢复密钥 raw/明文，用与 `crypto-vault.ts` 相同的原语生成信封与文档密文；再用 `tsx` 只读导入 `E:\code\ssh-tool\src\main\security\crypto-vault.ts` 的 `unwrapVaultKeyWithPassword`、`unwrapVaultKeyWithRecovery`、`decryptSyncDocument` 自检能解开。
    2. 生成 3 份文档夹具（空文档、含 relay 与分组的典型文档、含 secrets 密码的文档），`validate-fixtures.mjs` 导入桌面端 `syncDocumentV1Schema` 校验通过。
    3. 同时输出 JSON（C# 用）与 C++ 头（native 用）。
  - 验收：
    - [x] native：用向量解包与解密成功；相同 nonce/salt 加密结果逐字节等于向量
    - [x] Core：夹具经 Reader→Writer 后，`node tools/sync-vectors/validate-fixtures.mjs --file <输出>` 通过（`verify.ps1 -Interop` 执行）
  - 验证：`node tools/sync-vectors/generate.mjs --check`；`pwsh scripts/verify.ps1 -Quick -Interop`

- [x] **S05 VaultCrypto 桥与 C# 适配** `S` 📱
  - 依赖：S03、N09a
  - 参考：`01-DESIGN.md §6.2`
  - 产出：`src/SshTool.Native/Bridge/{VaultCrypto,VaultEnvelope,VaultSetupResult,DocumentEnvelope}.{h,cpp}`、`src/SshTool.Core/Sync/Vault/{IVaultCrypto,VaultKeyEnvelope,EncryptedDocumentEnvelope}.cs`、`src/SshTool.App/Platform/NativeVaultCrypto.cs`、`tests/SshTool.Core.Tests/Fakes/FakeVaultCrypto.cs`、调试页「保险库自检」
  - 要点：所有方法在后台线程执行，失败返回 null；FakeVaultCrypto 用可逆编码模拟并能模拟「密码错误」；自检页在真机跑黄金向量与桌面向量并显示 Argon2 耗时。
  - 验收：
    - [x] ARM/x64 构建通过
    - [ ] 📱 自检全部通过，记录 Argon2id 解锁耗时
  - 验证：`pwsh scripts/verify.ps1 -Arm`；真机

- [x] **S06 API 客户端：传输、端点与错误模型** `M`
  - 依赖：S01
  - 参考：`03-SYNC-PROTOCOL.md §2.1–2.3`；桌面端 `src/main/sync/api-client.ts`、`test/api-client.test.ts`
  - 产出：`src/SshTool.Core/Sync/Api/{IHttpTransport,HttpRequestData,HttpResponseData,ApiError,ApiErrorKind,ApiClient,ITokenStore}.cs`、`src/SshTool.Core/Sync/Api/Dtos/*.cs`、`src/SshTool.App/Platform/UwpHttpTransport.cs`、`tests/SshTool.Core.Tests/Fakes/FakeHttpTransport.cs`、测试
  - 要点：Base URL 规范化与 HTTP 许可；统一头；revision ETag 格式化与校验；错误解析（CodeUnknown、Retry-After 秒数/日期、RequestId）；全部端点类型化方法与 DTO 手写解析；`UwpHttpTransport`：关缓存/Cookie/自动重定向、超时取消、HEAD、`TryAppendWithoutValidation`、异常映射 network/timeout。
  - 验收：
    - [x] §10.1 ApiClient 1、2、9 + 每个 DTO 解析用例
  - 验证：`dotnet test`

- [x] **S07 API 客户端：刷新、重试与 HEAD 回退** `M`
  - 依赖：S06
  - 参考：`03-SYNC-PROTOCOL.md §2.4`
  - 产出：`ApiClient` 发送策略部分、测试
  - 要点：可重试请求/错误判定与退避；401 单飞刷新后重放；刷新请求不重试、失败标记 uncertain；终端鉴权错误清 token；HEAD 404 CodeUnknown → GET 回退；HEAD 元数据校验。注入 `ISleep`/`IClock` 便于测试。
  - 验收：
    - [x] §10.1 ApiClient 3–8 全部用例
  - 验证：`dotnet test`

- [x] **S08 认证存储与账号服务** `S`
  - 依赖：S07、D03
  - 参考：`03-SYNC-PROTOCOL.md §6.1、§7.1`；桌面端 `src/main/security/auth-store.ts`
  - 产出：`src/SshTool.Core/Sync/Auth/{AuthState,AuthStore,AuthService,IDeviceDescriptorProvider}.cs`、`src/SshTool.App/Platform/UwpDeviceDescriptorProvider.cs`、测试
  - 要点：AuthStore 基于 `ISecureFile`（`secure/auth.bin`）：`Session`、`Tokens`、`Save`、`MarkRefreshUncertain`、`CanRefresh`、`Clear`；AuthService：注册/登录（已登录时拒绝重复登录）、登出（服务端失败也清本地）、全部登出、改登录密码（成功后清本地）、注销账号、设备列表/改名/撤销（拒绝撤销本机）；设备描述：名称默认 `EasClientDeviceInformation.FriendlyName`，平台 `windows-mobile-arm` / `windows-uwp-<arch>`，版本取包版本。
  - 验收：
    - [x] 单测：持久化往返、uncertain 规则、重复登录保护、撤销本机被拒、改密后清本地
  - 验证：`dotnet test`

- [x] **S09 三方合并** `M`
  - 依赖：S02
  - 参考：`03-SYNC-PROTOCOL.md §8`；桌面端 `src/main/sync/sync-merge.ts`、`test/sync-merge.test.ts`
  - 产出：`src/SshTool.Core/Sync/{SyncMerge,SyncMergeConflict,SyncMergeResult}.cs`、测试
  - 要点：字段级合并（实体转 JObject 做通用字段合并，结果经 Reader 校验转回类型）；冲突记录 entity/id/field/sensitive/kind，不含值；updatedAt 取较大者。
  - 验收：
    - [x] §10.1 Merge 1–5，另加：preferences 冲突、secrets 部分键新增、两边删除同一实体
  - 验证：`dotnet test`

- [x] **S10 本地适配器（文档 ↔ 仓库）** `M`
  - 依赖：S02、D02、D03
  - 参考：`03-SYNC-PROTOCOL.md §5`；桌面端 `src/main/sync/sync-coordinator.ts` 末尾 `createLocalAdapter`
  - 产出：`src/SshTool.Core/Sync/{SyncLocalAdapter,ITunnelBusyProbe,SyncApplyException}.cs`、测试
  - 要点：Build（按 preferences 输出 secrets，私钥部分预留接口，S15 实现）；Apply 的两道保护、主机/分组/隧道本机专有字段保留、凭据「出现即覆盖、缺失即保留」、删除级联、`ChangeOrigin.Sync`。
  - 验收：
    - [x] 单测：B 端独有凭据保留、🏠 字段全部保留、分组删除清引用、relay 与 autoStart 保留、指纹变化拒绝、运行中隧道变更拒绝、未开启开关时文档不含 secrets
  - 验证：`dotnet test`

- [x] **S11 保险库缓存与协调器：账号与保险库流程** `M`
  - 依赖：S05、S08、S10
  - 参考：`03-SYNC-PROTOCOL.md §6.2、§6.3、§7.1、§7.2`（不含 Rotate）
  - 产出：`src/SshTool.Core/Sync/Vault/{VaultCacheState,VaultCacheStore,PendingVaultSetup,PendingUpload,SyncConflictSummary}.cs`、`src/SshTool.Core/Sync/{SyncCoordinator,SyncState,SyncPhase,VaultStatus}.cs`（第一部分）、测试
  - 要点：VaultCache 编解码（baseDocument 走 S02 读写器）与用户绑定；协调器 Initialize、登录/注册后流程、ProbeVault、SetupVault（先落盘 pending）、Unlock、Lock、DeleteVault、SetPreferences（拒绝直接关闭敏感开关）、Logout、ChangeAccountPassword、DeleteAccount；`StateChanged` 事件。
  - 验收：
    - [x] §10.1 Coordinator 1–5
  - 验证：`dotnet test`

- [x] **S12a 协调器：同步主流程与上传** `M`
  - 依赖：S11、S09
  - 参考：`03-SYNC-PROTOCOL.md §7.3`（PerformSync ①–④ 的非冲突路径、Upload、CommitRemote）
  - 产出：`SyncCoordinator` 同步部分、测试
  - 要点：单飞 SyncNow；pendingUpload 重放；HEAD 分支（无文档/无保险库/keyVersion 不一致/revision 回退）；无新版本时的上传条件；有新版本且本地不脏 → 应用远端；本地脏 → 合并 → 上传；上传前落盘 pendingUpload，特定错误清除；changeGeneration 竞争处理。
  - 验收：
    - [x] §10.1 Coordinator 6、9、20，以及「上传期间本地又改动 → dirty 保持并 250 ms 后再同步」
  - 验证：`dotnet test`

- [x] **S12b 协调器：冲突、首次导入与远端删除** `M`
  - 依赖：S12a
  - 参考：`03-SYNC-PROTOCOL.md §7.3`（SaveConflict、RemoteDeletionConflicts、ResolveConflict）
  - 产出：`SyncCoordinator` 冲突部分、测试
  - 要点：initial-import（无 baseDocument）、remote-deletion（干净设备）、merge-conflict；冲突摘要持久化并在重启后恢复；ResolveConflict 两种策略。
  - 验收：
    - [x] §10.1 Coordinator 7、14、15、16、17、18、21
  - 验证：`dotnet test`

- [x] **S13 协调器：轮换、历史、错误处理与重试** `M`
  - 依赖：S12b
  - 参考：`03-SYNC-PROTOCOL.md §7.2`（Rotate）、`§7.3`（Restore/Clear/MarkDirty）、`§7.4`
  - 产出：`SyncCoordinator` 剩余部分、测试
  - 要点：RotateVaultKey（敏感关闭 / 改同步密码）含失败回滚与 ambiguous 文案；RestoreRevision → SyncNow(use-remote)；ClearRevisions；ListRevisions/Devices 透传；HandleSyncError（终端鉴权、signed_out、offline/error、退避表与 Retry-After、`ITimerFactory`）；MarkDirty 防抖 3000 ms。
  - 验收：
    - [x] §10.1 Coordinator 8、10、11、12、13、19
    - [x] 退避时间序列断言 1/2/5/10/30/60/300 s，且 Retry-After 优先
  - 验证：`dotnet test`

- [x] **S14 同步触发器与应用接线** `S` 📱
  - 依赖：S13、P02
  - 参考：`03-SYNC-PROTOCOL.md §7.5`
  - 产出：`src/SshTool.Core/Sync/SyncTriggers.cs`、组合根接线、MainPage 同步图标绑定 `SyncState`、测试
  - 要点：仓库 Changed（origin=User 且实体为主机/分组/隧道或主机凭据）→ MarkDirty；启动、回前台（>30 s）、网络恢复、前台轮询、手动；后台停止轮询；同步图标映射（UI §5.1）。
  - 验收：
    - [x] Triggers 单测：Sync 来源不标脏、片段/外观变化不标脏、前台阈值、轮询启停
    - [ ] 📱 修改主机约 3 秒后自动同步
  - 验证：`dotnet test`；真机

- [x] **U15 登录与注册页** `S`
  - 依赖：S14
  - 参考：`02-UI-DESIGN.md §5.13`
  - 产出：`src/SshTool.App/Views/Sync/LoginPage.xaml(.cs)`、`src/SshTool.App/ViewModels/Sync/LoginViewModel.cs`、resw `Api_<CODE>` 文案、测试
  - 要点：登录/注册切换；明文 HTTP 风险 Banner（`allowHttp` 且 URL 为 http 时）；注册密码 ≥10 位、确认一致、邀请码可选；设备名默认值；三端共用账号说明；错误码中文映射；成功后跳状态页。
  - 验收：
    - [x] 单测：两份 resw 中 `Api_<CODE>` 覆盖 `03-SYNC-PROTOCOL.md §2.3` 全部码
    - [x] x64 对真实服务器注册测试账号并登录成功（记录到进度日志，不记录密码）
  - 验证：`pwsh scripts/verify.ps1`

- [x] **U16 保险库创建、解锁与恢复密钥** `M` 📱
  - 依赖：U15
  - 参考：`02-UI-DESIGN.md §5.13`（VaultSetupPage、VaultUnlockPage、RecoveryKeyDialog）
  - 产出：`src/SshTool.App/Views/Sync/{VaultSetupPage,VaultUnlockPage}.xaml(.cs)`、`src/SshTool.App/Dialogs/RecoveryKeyDialog.xaml(.cs)`、`src/SshTool.Core/Sync/Vault/RecoveryKeyInput.cs`、ViewModels、测试
  - 要点：同步密码 ≥8 位 + 确认；Argon2 进度遮罩；恢复密钥分行显示、复制、必须勾选「已保存」；解锁分段（同步密码/恢复密钥），恢复密钥输入规范化（去空白、前缀大写、格式通过才可提交）。保险库密钥解锁后持久化在 VaultCache，重启无需再次解锁，因此**不提供「记住同步密码」**。
  - 验收：
    - [x] RecoveryKeyInput 单测
    - [ ] x64：在已由桌面端创建保险库的账号上用同步密码解锁成功
    - [ ] 📱 真机创建或解锁成功
  - 验证：`pwsh scripts/verify.ps1`；真机

- [x] **U17 同步状态页** `M`
  - 依赖：U16
  - 参考：`02-UI-DESIGN.md §5.13` 状态 Pivot
  - 产出：`src/SshTool.App/Views/Sync/AccountSyncPage.xaml(.cs)`、`src/SshTool.App/ViewModels/Sync/AccountSyncViewModel.cs`、`src/SshTool.Core/Sync/SyncStatePresenter.cs`、测试
  - 要点：根据 AuthState / vault / phase 显示登录页、建库、解锁或状态卡；相位文案与图标；相对时间；立即同步；启用/自动同步开关；同步密码开关（开→关进入 U20 安全清理流程）；同步私钥开关在 S15 前禁用并说明。
  - 验收：
    - [x] SyncStatePresenter 单测覆盖全部 phase × vault 组合
  - 验证：`pwsh scripts/verify.ps1`

- [x] **U18 设备与历史版本** `S`
  - 依赖：U17
  - 参考：`02-UI-DESIGN.md §5.13` 设备/历史 Pivot
  - 产出：AccountSyncPage 设备、历史 Pivot 与对应 ViewModel
  - 要点：设备列表（本机徽标、重命名、撤销确认、本机禁止撤销）；历史列表（来源设备、时间、keyVersion；恢复确认；清空历史确认）；加载中/空/错误状态。
  - 验收：
    - [ ] 👤 x64 对真实服务器：重命名本机设备、恢复历史版本成功（记录到进度日志）
  - 验证：`pwsh scripts/verify.ps1`

- [x] **U19 同步冲突页** `S`
  - 依赖：U17
  - 参考：`02-UI-DESIGN.md §5.14`
  - 产出：`src/SshTool.App/Views/Sync/SyncConflictPage.xaml(.cs)`、`src/SshTool.Core/Sync/ConflictPresenter.cs`、测试
  - 要点：三种 reason 的标题/说明/按钮文案；远端摘要；字段列表（实体名称查找：本机 → 远端文档 → id；敏感字段显示「有变更」）；按钮调用 ResolveConflict 并处理错误。
  - 验收：
    - [x] ConflictPresenter 单测（名称查找顺序、敏感字段不含值、三种 reason 文案键）
  - 验证：`pwsh scripts/verify.ps1`

- [x] **U20 安全与账号操作流程** `M`
  - 依赖：U17
  - 参考：`02-UI-DESIGN.md §5.13`（安全清理、注销账号）；`03-SYNC-PROTOCOL.md §7.1、§7.2`
  - 产出：`src/SshTool.App/Views/Sync/{SecurityRotatePage,ChangeLoginPasswordPage,DeleteAccountPage}.xaml(.cs)` 与 ViewModels
  - 要点：关闭敏感同步 / 修改同步密码共用轮换页（账号登录密码 + 新同步密码 ×2 → RecoveryKeyDialog；失败回滚开关）；修改登录密码（成功后回登录页）；退出登录 / 退出所有设备确认；删除云端保险库（登录密码）；注销账号（登录密码 + 输入 `DELETE`）。
  - 验收：
    - [ ] 👤 x64 对测试账号跑通：关闭同步密码轮换、修改同步密码、删除保险库、重新建库（记录到进度日志）
  - 验证：`pwsh scripts/verify.ps1`

- [x] **S16 三端互通验收** `S` 👤📱
  - 依赖：U18、U19、U20
  - 参考：`03-SYNC-PROTOCOL.md §10.3`
  - 产出：`doc/INTEROP-REPORT.md`
  - 要点：按 §10.3 十个场景执行，逐条记录步骤、结果、发现的问题；问题转为新任务插入 M8 之前并在进度日志登记。
  - 验收：
    - [ ] 📱 场景 1–8、10 通过
    - [ ] 📱 场景 9（鸿蒙端参与）通过，或记录鸿蒙端侧已知差异
  - 验证：人工

---

## 8. M6 — 外观系统

- [x] **A01 外观解析与主机绑定** `S`
  - 依赖：D02、D04
  - 参考：`01-DESIGN.md §8.1`（AppearanceProfile）
  - 产出：`src/SshTool.Core/Appearance/{AppearanceResolver,AppearanceService}.cs`、测试
  - 要点：有效外观 = 主机 appearanceId → 全局 `defaultAppearanceId` → 内置默认；删除被引用外观时把引用主机改回 null（确认）；内置外观只读；外观变化事件（带受影响主机集合）。
  - 验收：
    - [x] 单测：解析优先级、删除回退、内置只读
  - 验证：`dotnet test`

- [x] **A02 内置主题** `S`
  - 依赖：A01
  - 参考：鸿蒙端 `common/model/models.ets` 的 `HARMONY_DARK_PALETTE`；各主题官方配色
  - 产出：`src/SshTool.Core/Appearance/BuiltInThemes.cs`、测试
  - 要点：Harmony Dark（默认）、Harmony Light、One Dark、Dracula、Nord、Solarized Dark、Solarized Light、Tokyo Night、GitHub Light；每套 16 色 + 前景/背景/光标/选区；id 固定 `builtin-<slug>`；注释注明配色来源。
  - 验收：
    - [x] 单测：9 套、每套 20 个色位合法 `#RRGGBB`、id 唯一（鸿蒙端仓库不在本机，Harmony 两套按 02-UI-DESIGN §3 Token 族推导并在注释注明）
  - 验证：`dotnet test`

- [x] **A03 外观列表、编辑页与实时预览** `M` 📱
  - 依赖：A02、T07
  - 参考：`02-UI-DESIGN.md §5.12`
  - 产出：`src/SshTool.App/Views/{AppearanceListPage,AppearanceEditPage}.xaml(.cs)`、ViewModels、`src/SshTool.Core/Appearance/SampleScreenBuilder.cs`、`ColorSwatchPicker` 完整版（HSV 滑块、新旧对比）、TerminalView 支持静态缓冲源、测试
  - 要点：预览使用 SampleScreenBuilder 生成的 16 字节单元格缓冲；编辑实时（≤100 ms）；未保存返回确认并还原；编辑内置主题自动复制；设为默认；已打开的终端收到外观变化后刷新调色板与字体度量（不重连）；主机编辑页「外观」下拉接入。
  - 验收：
    - [x] SampleScreenBuilder 单测（16 色、粗体、下划线、反色）
    - [ ] 📱 修改字号/配色后已打开终端即时生效
  - 验证：`pwsh scripts/verify.ps1`；真机

- [x] **A04 配色导入** `S`
  - 依赖：A03
  - 参考：`02-UI-DESIGN.md §5.12`
  - 产出：`src/SshTool.Core/Appearance/{ITermColorsParser,WindowsTerminalSchemeParser}.cs`、`tests/fixtures/themes/*`、导入按钮接线、测试
  - 要点：`.itermcolors`（plist XML，`Ansi 0 Color`…`Ansi 15 Color`、`Foreground/Background/Cursor/Selection Color`，分量 0–1）；Windows Terminal（单个 scheme 对象或含 `schemes` 数组的 settings.json，键 `black…brightWhite/foreground/background/cursorColor/selectionBackground`）；多个 scheme 时让用户选择；非法文件给出明确错误。
  - 验收：
    - [x] 两种格式各 ≥3 个真实主题文件解析成功；非法 XML/JSON/缺键报错且不抛未处理异常
  - 验证：`dotnet test`

- [x] **A05 应用主题与强调色设置** `S`
  - 依赖：X03、U14
  - 参考：`02-UI-DESIGN.md §1、§5.15`
  - 产出：SettingsPage 通用 Pivot 补齐、ThemeService 运行时切换
  - 要点：跟随系统/深色/浅色即时切换（无需重启）；使用系统强调色开关；终端内容不受应用主题影响（由外观决定）。
  - 验收：
    - [ ] 切换后已打开页面颜色正确（手测清单记入进度日志）
  - 验证：`pwsh scripts/verify.ps1`

---

## 9. M7 — 密钥、SFTP、端口转发、跳板

- [x] **K01 原生密钥工具** `M`
  - 依赖：N05
  - 参考：`01-DESIGN.md §6.1 keytool、§6.2 KeyTool`；`03-SYNC-PROTOCOL.md §9`
  - 产出：`native/core/crypto/keytool.{h,cpp}`、`src/SshTool.Native/Bridge/{KeyTool,KeyInfo}.{h,cpp}`、`native/tests/keytool_test.cpp`、`native/tests/fixtures/keys/*`（补充 ecdsa、加密 OpenSSH、PKCS#8 等）
  - 要点：生成 ed25519（openssh-key-v1 未加密，含 checkint 与 padding）、RSA 3072/4096（PEM）；解析 openssh-key-v1（直接取公钥 blob；读 cipher/kdf 名判定是否加密）、PEM/PKCS#8（OpenSSL，支持短语）；导出 `ssh-xxx AAAA… comment`；SHA256 指纹。
  - 验收：
    - [x] 所有夹具的类型/位数/是否加密判定正确，指纹等于 `ssh-keygen -lf` 期望值
    - [x] 生成的 ed25519 私钥能被 libssh2 `publickey_frommemory` 加载（单测内校验）
  - 验证：`pwsh scripts/verify.ps1 -Quick`

- [x] **K02 密钥管理页面** `M` 📱
  - 依赖：K01、D03、U03b
  - 参考：`02-UI-DESIGN.md §5.10`
  - 产出：`src/SshTool.App/Views/Keys/{KeysPage,KeyDetailPage}.xaml(.cs)`、`src/SshTool.App/Dialogs/{KeyImportDialog,KeyGenerateDialog}.xaml(.cs)`、`src/SshTool.App/ViewModels/Keys/*.cs`、`src/SshTool.Core/Keys/KeyImportService.cs`、测试
  - 要点：导入（文件/粘贴，≤256 KiB，加密时要短语，按指纹去重提示）；生成；详情（改名、复制/分享公钥、导出私钥二次确认、使用它的主机）；删除保护；启用 HostEdit 的 [导入]/[生成]。
  - 验收：
    - [x] KeyImportService 单测（大小上限、重复指纹、短语错误）
    - [ ] 📱 生成 ed25519 → 公钥加入服务器 `authorized_keys` → 用该密钥登录成功
  - 验证：`pwsh scripts/verify.ps1`；真机

- [x] **K03 应用内 Agent** `S`
  - 依赖：K02、D06
  - 参考：鸿蒙端 `cpp/ssh/agent.*`、`cpp/tests/agent_test.cpp`；`01-DESIGN.md §12.1`
  - 产出：`native/core/ssh/agent.{h,cpp}`、`native/tests/agent_test.cpp`、Bridge 暴露、SessionManager `authType=agent` 流程、设置项「Agent 密钥保留时间」
  - 要点：解锁后私钥留在 native 内存供多会话复用；超时（默认 15 分钟）与应用挂起时清除；认证时依次尝试已解锁密钥；无可用密钥时提示选择密钥解锁。
  - 验收：
    - [ ] 移植用例通过（超时清除、多会话复用）
  - 验证：`pwsh scripts/verify.ps1`

- [x] **S15 私钥同步** `M`
  - 依赖：K01、S13
  - 参考：`03-SYNC-PROTOCOL.md §4.1 私钥规则、§5.1、§5.2 第 4 条、§9`；桌面端 `src/main/sync/sync-serializer.ts`、`test/sync-serializer.test.ts`
  - 产出：`src/SshTool.Core/Sync/{IPrivateKeyInspector,PrivateKeySyncCodec}.cs`、SyncLocalAdapter 私钥部分、`tools/sync-vectors` 增加私钥指纹向量（用桌面端 `node_modules/ssh2` 的 `utils.parseKey().getPublicSSH()`）、测试、U17 私钥开关启用
  - 要点：出站编码与元数据；入站 header/格式/指纹校验（有短语或未加密时复算）；落库为 KeyEntry 并绑定主机，按指纹去重；关闭开关走轮换流程。
  - 验收：
    - [x] 桌面端 `sync-serializer.test.ts` 私钥相关场景的等价用例通过
    - [x] 向量：ed25519/rsa/ecdsa × openssh/pem × 加密/未加密 的指纹与桌面端一致
  - 验证：`pwsh scripts/verify.ps1 -Interop`

- [x] **F01 SFTP 原生层** `M`
  - 依赖：N06
  - 参考：`01-DESIGN.md §11.1`
  - 产出：`native/core/sftp/{sftp_session,transfer}.{h,cpp}`、`native/tests/sftp_test.cpp`
  - 要点：基于已认证会话打开 SFTP；readdir（名称、类型、大小、权限、mtime、链接目标）；stat/lstat/readlink/mkdir/rename/unlink/rmdir/setstat；分块读写 32 KiB、偏移续传、取消标志；新增 `6xx` SFTP 错误码（同步更新 C# 枚举、resw、对拍脚本、`01-DESIGN.md §6.3`）。
  - 验收：
    - [x] 集成（环境变量开启）：上传再下载 50 MB 文件 SHA256 一致；中断后续传一致
    - [x] 错误码对拍通过
  - 验证：`pwsh scripts/verify.ps1 -Quick`

- [x] **F02 SFTP 桥与传输队列** `M`
  - 依赖：F01、N09b
  - 参考：`01-DESIGN.md §11.1`
  - 产出：`src/SshTool.Native/Bridge/SftpSession.{h,cpp}`、`src/SshTool.Core/Sftp/{ISftpClient,RemoteEntry,TransferQueue,TransferItem,PermissionBits,RemotePath}.cs`、`src/SshTool.App/Platform/{NativeSftpClient,StorageFileStreams}.cs`、测试
  - 要点：异步 API + 进度事件（节流 200 ms）；TransferQueue（排队/进行/完成/失败/取消，并发 1，失败重试，速率滑动平均）；`PermissionBits` 八进制与 rwx 互转；`RemotePath` 规范化；本地文件流用 `IRandomAccessStream` 分块。
  - 验收：
    - [x] Core 单测：队列状态机、速率计算、权限互转、路径规范化
  - 验证：`pwsh scripts/verify.ps1`

- [x] **F03 SFTP 页面** `M` 📱
  - 依赖：F02、U07
  - 参考：`02-UI-DESIGN.md §5.17`
  - 产出：`src/SshTool.App/Views/SftpPage.xaml(.cs)`、`src/SshTool.App/Dialogs/{PermissionsDialog,RenameDialog}.xaml(.cs)`、`src/SshTool.App/ViewModels/SftpViewModel.cs`
  - 要点：面包屑、排序、隐藏文件开关、行菜单操作、上传（多选）、下载（FileSavePicker）、新建文件夹、递归删除确认、传输面板；入口：终端菜单与主机菜单（复用已有连接或新建）。
  - 验收：
    - [ ] 📱 上传手机照片到服务器；下载日志文件到手机并能打开
  - 验证：`pwsh scripts/verify.ps1`；真机

- [x] **F04 原生转发：本地监听、direct-tcpip 与远程监听** `M`
  - 依赖：N06
  - 参考：`01-DESIGN.md §11.2`
  - 产出：`native/core/fwd/{local_listener,direct_tcpip,remote_listen,pump}.{h,cpp}`、`native/tests/fwd_test.cpp`
  - 要点：在会话 I/O 线程上非阻塞监听与双向泵（背压：通道写阻塞时暂停读 socket）；每条隧道统计（活跃、累计、上下行字节）；remote 接受通道后连接目标；关闭时优雅收尾。
  - 验收：
    - [x] pump 单测（假通道：部分写、EAGAIN、EOF 半关闭）
    - [ ] 集成（环境变量开启）：本地转发到远端 HTTP 服务取回内容
  - 验证：`pwsh scripts/verify.ps1 -Quick`

- [x] **F05 SOCKS5 与隧道运行时** `M`
  - 依赖：F04
  - 参考：桌面端 `src/main/tunnel/{socks5,manager,tunnel}.ts`；`01-DESIGN.md §11.2`
  - 产出：`src/SshTool.Core/Forwarding/{Socks5Parser,TunnelManager,TunnelStatus,TunnelStats}.cs`、`src/SshTool.Native/Bridge/Forwarder.{h,cpp}`、`src/SshTool.App/Platform/NativeForwarder.cs`、测试
  - 要点：SOCKS5 问候（仅无认证）、CONNECT（IPv4/域名/IPv6）、回复码；TunnelManager 状态机与自动重连退避、autoStart、`IsBusy(tunnelId)` 实现 S10 的 `ITunnelBusyProbe`；relay 拒绝启动；每秒速率统计。
  - 验收：
    - [x] Socks5Parser 单测（分片到达、非法版本、不支持命令）；TunnelManager 状态机单测
  - 验证：`pwsh scripts/verify.ps1`

- [x] **F06 隧道界面** `M` 📱
  - 依赖：F05、U02
  - 参考：`02-UI-DESIGN.md §5.3、§5.18`
  - 产出：`src/SshTool.App/Views/Main/TunnelsPivot.xaml(.cs)`、`src/SshTool.App/Views/TunnelEditPage.xaml(.cs)`、ViewModels、HostEditPage 高级 Pivot 隧道区
  - 要点：按隧道分组列表、启停开关、运行状态与速率（1 s 刷新）、relay「仅桌面端运行」、首次开启 local/dynamic 的回环隔离提示；编辑页四种类型字段显隐与校验、路径图示文本。
  - 验收：
    - [ ] 📱 远程转发（-R）把手机可达服务暴露到服务器端口并访问成功；动态转发供局域网电脑作 SOCKS 代理访问成功
  - 验证：`pwsh scripts/verify.ps1`；真机

- [x] **F07 ProxyJump 多级跳板** `M` 📱
  - 依赖：F04、D06
  - 参考：`01-DESIGN.md §11.3`；鸿蒙端 `docs/DESIGN.md §3.3`
  - 产出：`native/core/fwd/jump_transport.{h,cpp}`、`src/SshTool.Core/Sessions/JumpChainPlanner.cs`、SessionManager 链式连接、测试
  - 要点：`LIBSSH2_CALLBACK_SEND/RECV` 把上级 direct-tcpip 通道作为下级会话传输；Planner 计算连接顺序、检测环、深度 ≤5；每一跳独立的主机密钥与认证提示（对话框标题注明第几跳）；任一跳断开时下级报错并从最上级重连。
  - 验收：
    - [x] Planner 单测；jump_transport 假通道单测
    - [ ] 📱 两级跳板连接成功；断开中间跳后下级进入重连而非挂死
  - 验证：`pwsh scripts/verify.ps1`；真机

---

## 10. M8 — 打磨与发布

- [x] **Q01 性能基准与优化** `M` 📱
  - 依赖：U12、A03
  - 参考：`01-DESIGN.md §15`
  - 产出：`src/SshTool.App/Views/Debug/PerfPage.xaml(.cs)`（FPS/帧耗时叠加、内存、场景按钮）、`doc/PERF-REPORT.md`、针对性优化提交
  - 要点：场景：`head -c 1048576 /dev/urandom | base64`、`yes | head -n 200000`、vim 大文件翻页、100 主机列表、4 会话 × 5000 行回滚内存；记录 Release ARM 数据；不达标项剖析优化并复测。
  - 验收：
    - [ ] 📱 §15 指标全部达标，或报告中说明差距与原因
  - 验证：真机

- [x] **Q02 稳定性与泄漏治理** `M` 📱
  - 依赖：Q01
  - 参考：`01-DESIGN.md §6.4`
  - 产出：PerfPage 增加「连接/断开 ×100」与「长稳 4 小时」脚本、结果追加到 `doc/PERF-REPORT.md`
  - 要点：记录句柄数（调试构建 native 导出计数）、托管内存、native 分配计数；修复发现的泄漏；挂起/恢复 20 次循环。
  - 验收：
    - [ ] 📱 100 次循环后内存回落到基线 +10% 以内；4 小时会话无崩溃
  - 验证：真机

- [x] **Q03 安全自查** `S` 👤
  - 依赖：S16、K02
  - 参考：`01-DESIGN.md §12`；`03-SYNC-PROTOCOL.md §11`
  - 产出：`doc/SECURITY-REPORT.md`
  - 要点：设备门户下载 LocalFolder，搜索测试密码/私钥片段/恢复密钥；日志抽查；抓包（电脑热点 + Wireshark）确认文档为密文并记录登录明文风险；删除主机后凭据消失；WACK 运行结果；OpenSSL、libssh2 版本 CVE 检查。
  - 验收：
    - [ ] 报告每项有结论；发现的问题已修复或登记为任务
  - 验证：人工

- [x] **Q04 英文本地化** `S`
  - 依赖：U20、F06、K02
  - 参考：`02-UI-DESIGN.md §7`
  - 产出：`Strings/en-US/Resources.resw` 补齐、`tests/SshTool.Core.Tests/ResourceParityTests.cs`、`scripts/check-hardcoded-text.ps1`
  - 要点：两份 resw 键集合一致；XAML 无硬编码文案（扫描 `Text="[^{]`、`Content="[^{]`、`Header="[^{]`，Debug 页面豁免）；英文下主要页面不截断。
  - 验收：
    - [x] 键一致单测通过；扫描脚本加入 verify 且通过
  - 验证：`pwsh scripts/verify.ps1`

- [ ] **Q05 无障碍** `S` 📱
  - 依赖：Q04
  - 产出：各页面 `AutomationProperties.Name` / `HelpText` 补齐
  - 要点：图标按钮、键条按键、状态点、开关都有可读名称；讲述人可完成「添加主机 → 连接 → 断开」。
  - 验收：
    - [ ] 📱 讲述人走通主流程
  - 验证：真机

- [x] **Q06 关于页、开源许可与隐私说明** `S`
  - 依赖：U14
  - 参考：`01-DESIGN.md §3.2`；`02-UI-DESIGN.md §5.19`
  - 产出：`src/SshTool.App/Views/{AboutPage,LicensesPage}.xaml(.cs)`、`src/SshTool.App/Assets/Licenses/*.txt`、`OPEN_SOURCE_LICENSES.md`、`PRIVACY.md`
  - 要点：列出全部依赖名称、版本、许可证全文；隐私说明：本地存储内容、同步内容与加密方式、服务器地址与明文 HTTP 风险、日志不含敏感信息。
  - 验收：
    - [x] 许可清单与实际 NuGet / vendored 版本一致
  - 验证：`pwsh scripts/verify.ps1`

- [x] **Q07 图标、磁贴与启动画面** `S` 📱
  - 依赖：X02
  - 产出：`tools/assets/{icon.svg,generate.ps1}`、`src/SshTool.App/Assets/*` 全尺寸资源、清单更新
  - 要点：Square44x44（含 targetsize-16/24/32/48 与 altform-unplated）、Square71x71、Square150x150、Wide310x150、Square310x310、StoreLogo、SplashScreen，各 scale-100/125/150/200/400；深浅开始屏幕都清晰；启动画面背景色 = 深色 `AppBgBrush`。
  - 验收：
    - [ ] 📱 开始屏幕各磁贴尺寸与应用列表图标清晰
  - 验证：真机

- [ ] **Q08 Continuum 体验打磨** `S` 📱
  - 依赖：U12
  - 参考：`02-UI-DESIGN.md §3、§5.7`
  - 产出：自适应与鼠标模式细节修正
  - 要点：鼠标模式行高、键条默认隐藏、窗口尺寸变化时布局稳定、快捷键冲突、右键菜单位置、断开 Continuum 时会话保活。
  - 验收：
    - [ ] 📱 Continuum 下完成一次完整运维流程（2 个标签、分屏、SFTP 下载、片段发送）无布局问题
  - 验证：真机

- [ ] **Q09 打包、签名与侧载指南** `S` 👤
  - 依赖：Q07
  - 产出：`scripts/{package-arm,deploy-phone,bump-version}.ps1`、`doc/INSTALL.md`
  - 要点：Release ARM（可选 x86/x64）appxbundle + 依赖包目录；测试证书生成与签名；`deploy-phone.ps1 -Ip -Pin`；INSTALL.md：开发者模式、设备门户网页安装、WinAppDeployCmd、证书信任、升级时数据保留说明。
  - 验收：
    - [ ] 👤 按 INSTALL.md 在另一台（或恢复出厂设置的）Lumia 上从零安装成功
  - 验证：人工

- [ ] **Q10 v1.0.0 发布前总检** `S` 👤📱
  - 依赖：Q01、Q02、Q03、Q04、Q05、Q06、Q08、Q09、Q11、F03、F06、F07、S15、K03、A04、A05、U06、U10、U13
  - 产出：`doc/RELEASE-CHECKLIST.md`、git tag `v1.0.0`
  - 要点：清空「真机验收待办」（逐条验证或明确转入后续版本）；设计文档与实现一致性复核；版本号 1.0.0.0；Release 包归档。
  - 验收：
    - [ ] 📱 真机验收待办清空或每条都有明确处置
    - [ ] `pwsh scripts/verify.ps1 -Arm -Interop` 全绿
  - 验证：人工

- [ ] **Q11 libssh2/OpenSSL 安全升级（CVE-2026-55200 等）** `M` 📱
  - 依赖：—（最高优先级：v1.0.0 发布前必须完成，Q10 依赖本任务）
  - 参考：`doc/SECURITY-REPORT.md §6`；上游修复 commits `7acf3df`、`a2ed82d` 等
  - 产出：`native/third_party/libssh2` 安全补丁（`PATCHES.md` 登记）、OpenSSL 3.6.4 重建（`scripts/build-openssl.ps1`）、全量回归
  - 要点：cherry-pick 上游修复——CVE-2026-55200（packet_length 越界写，CVSS 9.2，认证前可触发，PoC 已公开）、CVE-2026-66033（AES-GCM 整数下溢，认证前可触发）、CVE-2026-66032（sftp_open 双重释放）等 2026-07-24 批量披露的 4 个高危（1.11.1 受影响，上游尚无修复版）；OpenSSL 3.6.3 → 3.6.4（2026-08-25 公告，11 个 Moderate CVE，本应用攻击面不涉及但顺手升级）；重建后全量回归。
  - 验收：
    - [ ] `PATCHES.md` 登记补丁清单与来源 commits；`verify.ps1` 全绿（含 native 全量测试）
    - [ ] 📱 SP03 真机连接复测（打补丁后握手/认证/通道正常）
  - 验证：`pwsh scripts/verify.ps1`；真机

---

## 11. M9 — 优化与债务清理

> 依据：`06-OPT-AUDIT.md`（2026-09-21 三层逐文件核查）。本里程碑**不加功能、不动同步线格式、不做 UI 换皮**，
> 只做可度量的优化与债务清理，为 Q01 真机采数（`01-DESIGN.md §15`）与 Q02 长稳扫清已知障碍。
> 顺序即优先级；O01/O02/O03 是 P0，应先做。

- [x] **O01 删除 `pendingOutput_` 过渡取数链** `S`
  - 依赖：—
  - 参考：`06-OPT-AUDIT.md §3 P0-1`；`01-DESIGN.md §6.2`、§7.2、§15
  - 产出：`src/SshTool.Native/Bridge/SshSession.{h,cpp}`、`src/SshTool.Core/Sessions/ISshSession.cs`、`src/SshTool.App/Platform/NativeSshSession.cs`、`tests/SshTool.Core.Tests/Fakes/FakeSshSession.cs`
  - 要点：
    1. 删除 `pendingOutput_` / `outputMutex_` / `FetchPendingOutput()`（Native + 接口 + 透传 + Fake + 对应单测）。`SshSession.h:28` 自述其为「T03 接管后移除」的过渡 API，T03 已接管（渲染走 `TerminalScreen.Revision` + `CopyDirtyRows`）。
    2. 理清 `ContentDirty` 契约：`markConsumed()` 原本只在 `FetchPendingOutput` 里调用，删除后标志将永久为真。二选一并在 `SshSession.h` 注释写明：要么保留事件但每次 `OnShellData` 都投递（去掉合并标志），要么连同 `DirtyCoalescer` 一起移除、`Views/Debug/DebugConnectPage.xaml.cs` 改为轮询 `Revision`。
       **实做取第三条（更贴 §4.2）**：`DirtyCoalescer` 改为自复位的 16 ms 时间窗（无需消费者回调），并补上 §4.3 时序里一直缺失的 App 侧接线——`TerminalView` 订阅 `ContentDirty` → `FrameScheduler.Wake()`。原因：不接线的话，终端失焦后帧调度器约 500 ms 退订（30 帧空闲阈值先于 530 ms 闪烁到期），新到的远端输出没有任何东西能把它唤回来。
    3. 不要顺手改 `TerminalScreen.Feed` 与脏行拷贝路径——那是生产渲染路径，本任务只拆过渡链。
  - 验收：
    - [x] 全仓 `grep -r FetchPendingOutput` 仅剩两处文档注释（`SshSession.h` 与 `dirty_coalescer.h` 的「为什么删」说明），代码零命中
    - [x] `native/tests` 中涉及该 API 的断言一并清理（`bridge_logic_test` 的 3 条 consume-rearm 用例 → 6 条时间窗用例），ctest 309 全过
    - [x] `pwsh scripts/verify.ps1` 全绿（Core 1429 + native 309，7 步全通过）
    - [ ] 📱 PerfPage「Q02 连接/断开 ×100」与长稳复测，内存 Δ 与 `sessions/threads/sockets/screens` 回填 `doc/PERF-REPORT.md §5.1`
  - 验证：`pwsh scripts/verify.ps1`；真机 Q02

- [x] **O02 `SettingsRepository.Set` 相等值短路** `S`
  - 依赖：—
  - 参考：`06-OPT-AUDIT.md §3 P0-3`；`05-CODE-AUDIT-UI-REDESIGN.md §C-07`
  - 产出：`src/SshTool.Core/Storage/SettingsRepository.cs`、`tests/SshTool.Core.Tests/Storage/SettingsRepositoryTests.cs`
  - 要点：
    1. `Set` 校验通过后先经 `Read(def)` 取当前有效值，`Equals(current, value)` 则直接返回：不写 `_store`、不触发 `Changed`。
       **实做与本条有一处偏差**：比较对象取**存储里的原始值**而非 `Read()` 的有效值，且要求它本身合法——
       存了非法值时 `Read` 会回退到默认值但不覆写存储，若拿有效值比较，`Set(key, 默认值)` 会被误判同值而跳过，
       非法值就永远留在存储里。判据改为「存的值合法且相等才跳过」，顺带保留了改动前「写默认值能修复非法值」的行为。
    2. 注意「未写过的键」的当前有效值是 `def.DefaultValue`——首次显式写入默认值时应保持不写不广播（与读回结果一致），并在注释中写明该语义。
    3. 页面侧 150 ms 去抖（`Views/SettingsPage.xaml.cs:35,54,73,279`）已实现，本任务不动。
  - 验收：
    - [x] 新增单测 9 条（`CountingSettingsStore` 记账）：同值不写不广播、Slider 拖动形状只写真实变化、异值照常写并广播、
      首次写入等于默认值被跳过、非法值/类型不符时写默认值仍落盘修复、键缺失时照常写、字符串按值比较、非法值仍抛
    - [x] `pwsh scripts/verify.ps1` 全绿（Core 1438、7 步全通过）
  - 验证：`dotnet test tests/SshTool.Core.Tests`

- [x] **O03 页面级订阅收口（内存泄漏）** `M`
  - 依赖：—
  - 参考：`06-OPT-AUDIT.md §3 P0-2`；既有正确范式 `ViewModels/TerminalViewModel.cs` 的 `Detach()`、`ViewModels/TunnelsViewModel.cs:206`
  - 产出：`src/SshTool.App/Views/Main/SessionsPivot.xaml.cs`、`Controls/SessionsPane.xaml.cs`、`ViewModels/{HostList,Main,Settings}ViewModel.cs`、`ViewModels/Sync/{AccountSync,SecurityRotate,VaultSetup,VaultUnlock}ViewModel.cs`、对应页面的 `OnNavigatedFrom`
  - 要点：
    1. 视图侧：`SessionsPivot.Attach` 的匿名 lambda 改具名处理器并提供 `Detach()`；`Detach` 里同时把 `ItemsSource` 置空（单例 `ObservableCollection` 会经 CollectionChanged 攥住已弃用的 ListView）。`Controls/SessionsPane` 同样处理。
    2. VM 侧：为每个在构造函数里订阅应用级单例的 VM 增加 `Detach()`，解除全部 `+=`；由拥有它的页面在 `OnNavigatedFrom` 调用。
    3. `MainPage` 每次导航回来都 `new MainViewModel()`（全仓无 `NavigationCacheMode`），`MainViewModel.Detach()` 需级联 `Hosts.Detach()`；`Sessions` 是 `ServiceRegistry` 单例，**不要** Detach 它本身，只拆视图侧订阅。
    4. `Detach` 必须幂等（重复 GoBack / Frame 重复回调）。
  - 验收：
    - [x] 上述文件中每个订阅应用级单例的 `+=` 都有对应 `-=`
    - [x] ~~新增 Core 侧可测部分的回归~~ **改为门禁脚本**：App 层 VM 在 UWP 程序集里，`tests/SshTool.Core.Tests`（net8）
      引用不到，写不出真单测。改为新增 `scripts/check-subscriptions.ps1`（verify ④c）：受管事件的具名订阅必须同文件配对 `-=`，
      匿名委托订阅一律报错；同寿命的 5 处逐条豁免并写明理由。已用临时探针文件双向验证（有违例 EXIT=1、删除后 EXIT=0）
    - [x] `pwsh scripts/verify.ps1` 全绿（8 步，新增 ④c）
    - [ ] 📱 首页↔终端页往返 50 次，内存无明显增长、列表不重复刷新（`05` §10 的对应条目）
  - 验证：`pwsh scripts/verify.ps1`；真机

- [x] **O04 `NavigationLifetime` 覆盖剩余页面** `M`
  - 依赖：O03
  - 参考：`06-OPT-AUDIT.md §3 P1-4`；范式见 `Views/TerminalPage.xaml.cs`
  - 产出：`src/SshTool.App/Views/Sync/*.xaml.cs`（9 页）、`Views/{Settings,Main,Snippets,KnownHosts,GroupManage}Page.xaml.cs`
  - 要点：
    1. `OnNavigatedTo` → `int gen = _lifetime.Begin()`；每个 `await` 之后 `if (!_lifetime.IsCurrent(gen)) return;`；订阅一律经 `_lifetime.Track(() => … -= …)`；`OnNavigatedFrom` → `_lifetime.End()`。
    2. 已落地的范式是**整型世代号**，不是 `05` §C-02 建议的 `CancellationTokenSource`；保持一致，不要两套。
    3. Sync 组优先（网络 I/O 最长）；按 Sync 组 / 其余页分两次提交。
  - 验收：
    - [x] ~~含 `OnNavigatedTo` 的非 Debug 页面全部接入~~ **改为按「是否真有页面级 await」接入**：
      逐页核查后只有 3 页存在「await 之后回头写本页 XAML / 导航」的 C-02 形态，已接入
      （`SettingsPage`、`Sync/VaultSetupPage`、`Sync/SecurityRotatePage`）。其余页面写明豁免理由：
      `Sync/AccountSyncPage` 的 6 处 await 全是模态 ContentDialog，**用户已确认的动作必须执行完**，
      加世代守卫反而会静默丢弃撤销设备/删除这类确认操作（页面侧的陈旧 UI 更新已由 O03 的 VM `Detach` 挡住）；
      `MainPage` 唯一的 await 是退出确认（之后只调 `Application.Exit`，不碰 XAML）；
      `Snippets/KnownHosts/GroupManage/About/Licenses/Login/VaultUnlock/ChangeLoginPassword/DeleteAccount/DeleteVault/SyncConflict`
      代码隐藏里零 await，异步都在页面级 VM 内，已由 O03 收口。给不需要的页面加世代对象是空仪式，不做。
    - [x] Core 侧补 `NavigationLifetime.Current`（事件处理器在 await 前取世代号）+ 4 条单测
    - [x] 顺带修掉 `SecurityRotatePage` 的恢复密钥缺陷（见下）
    - [x] `pwsh scripts/verify.ps1` 全绿（8 步，Core 1442）
  - 验证：`pwsh scripts/verify.ps1`

- [x] **O05 fire-and-forget 统一走 `Forget`** `S`
  - 依赖：O04
  - 参考：`06-OPT-AUDIT.md §3 P1-5`；`src/SshTool.Core/Common/TaskExtensions.cs`
  - 产出：28 个文件中的 48 处 `var ignore = …`；`src/SshTool.App/Infrastructure/DispatcherHelper.cs`
  - 要点：
    1. 机械替换为 `X().Forget("模块.动作", AppLog.Logger)`；context 用「类名.方法」，不得拼入主机名/路径/用户名。
    2. 用户命令类调用优先改 `AsyncCommand`（执行期间管理 `CanExecute`），而不是简单加 `Forget`。
       **本次未做**：这批调用点几乎全是代码隐藏里的 `Click` 处理器（行菜单、Flyout 项），
       改 `AsyncCommand` 要连带把 XAML 换成命令绑定，属于 V04/V05 的表单与页面改造范围；
       本任务只做「异常可观察」这一层，不动交互结构。
    3. `DispatcherHelper.cs:92` 是 UI 封送原语，其自身失败也要可观察；注意别在日志失败路径上递归（`TaskExtensions` 注释已说明 `FileLogger` 的例外）。
  - 验收：
    - [x] 非 Debug 代码中 `var ignore = ` 计数为 0（47 处 → 0，覆盖 22 个文件）
    - [x] 门禁化：`scripts/check-subscriptions.ps1` 增规则 C（`var ignore =` 即报错），随 verify ④c 执行；
      三条规则均用临时探针文件验证过会报错，清空后恢复通过
    - [x] `pwsh scripts/verify.ps1` 全绿（8 步）
  - 验证：`pwsh scripts/verify.ps1`

- [x] **O06 `UwpHttpTransport` 复用 filter 与 client** `S`
  - 依赖：—
  - 参考：`06-OPT-AUDIT.md §3 P1-6`；`03-SYNC-PROTOCOL.md §2.1`
  - 产出：`src/SshTool.App/Platform/UwpHttpTransport.cs`
  - 要点：
    1. `HttpBaseProtocolFilter` 与 `HttpClient` 提升为只读字段（一次构造、长期复用；`Windows.Web.Http.HttpClient` 线程安全），`HttpRequestMessage`/`HttpResponseMessage` 仍按请求 `using`。
    2. NoCache / NoCookies / `AllowAutoRedirect=false` 语义一字不改（§2.1：凭证只走 `Authorization` 头）。
    3. 超时与取消语义保持：超时抛 `TimeoutException`，调用方取消原样抛 `OperationCanceledException`。
  - 验收：
    - [x] 现有 `ApiClient` 相关测试全过（Core 侧用假传输，不受影响）
    - [x] `pwsh scripts/verify.ps1` 全绿（8 步）
    - [ ] 📱 真机登录 + 一次完整同步周期成功（Wi-Fi 与蜂窝各一次）
  - 验证：`pwsh scripts/verify.ps1`；真机

- [x] **O07 同步路径去重复序列化** `M`
  - 依赖：—
  - 参考：`06-OPT-AUDIT.md §3 P1-7`；`03-SYNC-PROTOCOL.md §3.1、§7.3、§8`；`01-DESIGN.md R8`
  - 产出：`src/SshTool.Core/Sync/SyncCoordinator.cs`、`Sync/Protocol/SyncDocumentWriter.cs`、`Sync/SyncMerge.cs`
  - 要点：
    1. ~~`PendingUpload` 缓存创建时已算出的规范 JSON，`SameContent` 只重建 `local` 一侧。~~
       **核查后放弃，是坏交易**：`SameContent` 比的是 `omitUpdatedAt` 变体，而 `UploadAsync` 已算出的
       是含 `updatedAt` 的正式 JSON，两者不通用；要缓存就得在每次上传时**额外**再跑一遍 `BuildJson`，
       把「重放时才付一次」的成本改成「每次上传都付」。而重放只在响应丢失后发生，极少。
    2. 核实 `BuildLocalDocumentAsync()` 返回的是独占快照；是则去掉 `Document = doc.Clone()` 的整图深拷贝。
    3. `SyncMerge` ~~改「模型 → `JObject`」直转~~ **只砍掉输入侧的重复校验**：新增
       `SyncDocumentWriter.WriteUnvalidated`（跳过 `Validate` 与 2 MiB 检查）供三份合并输入使用，
       结尾的 `SyncDocumentReader.Read` 全量校验保留（R8 兜底）。
       **不做直转的理由**：另写一个「模型 → JObject」转换会让 schema 出现第二个事实来源，
       漏一个字段就是静默的同步数据丢失，代价远大于省下的 CPU。
    4. **上传字节必须零偏差**：本任务只改「算几遍」，不改「算成什么」。
  - 验收：
    - [x] Sync 相关单测（Coordinator/Merge/Writer/Reader）全过，无新增断言放宽（Core 1442）
    - [x] `pwsh scripts/verify.ps1 -Quick -Interop` 全绿（桌面端 zod 校验 3 份原文 + Core 重写输出 3 份，上传字节零偏差）
    - [x] `pwsh scripts/verify.ps1` 全绿（8 步）
    - [x] 核查中发现更大的一项（`VaultCacheStore.State` 每次读深拷贝整份文档，33 处调用），
      已立为 **O15** 并写入 `06-OPT-AUDIT.md §3 P1-15`，不并入本任务
  - 验证：`pwsh scripts/verify.ps1 -Quick -Interop`

- [ ] **O15 `VaultCacheStore.State` 免深拷贝读法** `M`
  - 依赖：O07
  - 参考：`06-OPT-AUDIT.md §3 P1-15`（O07 期间发现）
  - 产出：`src/SshTool.Core/Sync/Vault/VaultCacheStore.cs`、`Vault/VaultCacheState.cs`、`Sync/SyncCoordinator.cs`
  - 要点：
    1. `State` 的 getter 每次都 `_state.Clone()`，而 `Clone()` 深拷贝 `BaseDocument` /
       `PendingUpload.Document` / `ConflictRemoteDocument` 三份完整文档；`SyncCoordinator` 里 33 处调用，
       绝大多数只读一个标量。
    2. 保留 `State` 的深拷贝语义给确实要改文档的调用方；另加不拷贝文档的投影读法
       （锁内取标量，或 `CloneWithoutDocuments()`），把只读标量的调用点迁过去。
    3. **逐个确认调用点是否真的不碰文档**——错搬一处就是空引用或读到旧文档；迁移按批次提交。
  - 验收：
    - [ ] 只读标量的调用点不再触发文档深拷贝；需要文档的调用点行为不变
    - [ ] Sync 全部单测通过；`pwsh scripts/verify.ps1 -Quick -Interop` 全绿
  - 验证：`pwsh scripts/verify.ps1 -Quick -Interop`

- [x] **O08 `Repository<T>` id 索引** `S`
  - 依赖：—
  - 参考：`06-OPT-AUDIT.md §3 P1-8`
  - 产出：`src/SshTool.Core/Storage/Repository.cs`、`Storage/Repositories/KnownHostRepository.cs`、`Sessions/SessionManager.cs`
  - 要点：
    1. `Repository<T>` 内维护 `Dictionary<string,int>`（id → `_items` 下标），`_items` 整体替换处（`AddAsync`/`UpdateManyAsync`/`RemoveManyAsync`/`ReplaceAllAsync`/`EnsureLoadedCoreAsync`）统一重建；`FindIndex` 改查字典。
    2. `KnownHostRepository` 另加 `(host,port)` 索引，让 `SessionManager.FindKnownAsync` 与跳板链每跳的查找降为 O(1)。
    3. 纯内存结构，不动持久化格式与 `IEntityCodec`。
  - 验收：
    - [x] 现有仓库单测全过；新增 7 条回归：索引跨 Add/AddMany/Update/Remove/ReplaceAll 全程正确、
      冷加载后可查、null/空 id 返回默认、重复 id 仍取第一个；`KnownHostRepository.FindAsync` 的
      大小写不敏感/端口精确匹配、写入后索引失效重建、端口与主机名拼键无歧义
    - [x] `pwsh scripts/verify.ps1` 全绿（8 步，Core 1449）
  - 验证：`dotnet test tests/SshTool.Core.Tests`

- [x] **O09 Native 终端热路径去重复解析** `M`
  - 依赖：—
  - 参考：`06-OPT-AUDIT.md §3 P1-9`；`01-DESIGN.md §7.1、§7.5`；`PERF-REPORT.md §2`
  - 产出：`native/core/term/vterm_screen.{h,cpp}`、`native/tests/vterm_screen_test.cpp`
  - 要点：
    1. ~~删掉 `feed()` 里的手写 CSI 扫描器，三个模式位改从 libvterm 的 `VTermState` 读取~~
       **前提不成立，扫描器必须保留**：核对 `native/third_party/libvterm/include/vterm.h:253-279`，
       `VTermProp` 只有 `CURSORVISIBLE/CURSORBLINK/ALTSCREEN/TITLE/ICONNAME/REVERSE/CURSORSHAPE/MOUSE/FOCUSREPORT`，
       其中 `MOUSE` 也只区分 1000/1002/1003，**不含 1006 的编码开关**；DECCKM(1) 与 bracketed paste(2004)
       更是完全没有读取接口。libvterm 并没有「已经在跟踪」这三个值，这一遍扫描省不掉。
       改为**修掉扫描器里的真缺陷**（见验收），并去掉常见路径上的拷贝。
    2. ~~`refreshSoftWrapFlags()` 改为只扫 damage rect 覆盖的行~~ **不做**：实测它在 48×30 网格上是
       30 次指针写 + 29 次 `vterm_state_get_lineinfo`，约 60 次操作；为此引入 damage 区间跟踪，
       复杂度与出错面远大于收益。审查里把它列为「稳定税」是高估了。
    3. 软换行标记的语义（写在行末格的 bit9）不得改变——`SelectionModel` 的复制逻辑依赖它（§7.1、§7.6）。
  - 验收：
    - [x] **修掉扫描器的静默丢模式缺陷**：旧实现在扫描**前**把缓冲截成「最后 256 字节」
      （`csiPending_.erase(0, size - 256)`），一次 32 KB 读块里靠前的 DECSET 被直接丢弃——
      vim 之类先发模式切换再刷满屏正好踩中，表现为方向键/粘贴行为时灵时不灵。
      现在扫全量，只把未完成的尾巴留到下次；carry 为空时直接扫入参、不拷贝（绝大多数 feed 走这条）；
      畸形的超长未完成序列超过 256 字节即丢弃，防无界增长
    - [x] `vterm_screen_test` 新增 6 条（三模式开关、单序列多参数、**大块前部 DECSET 不丢**（先写后修的回归）、
      多次大块不冲掉已生效模式、跨 feed 边界拼接、畸形长序列不增长），42 条全过；ctest 315 全过
    - [x] `pwsh scripts/verify.ps1` 全绿（8 步）
    - [ ] 📱 PerfPage 场景①②③ 的「喂 x ms/帧」较优化前下降，数据回填 `doc/PERF-REPORT.md §3.1`
  - 验证：ctest；`pwsh scripts/verify.ps1`；真机 Q01

- [x] **O10 Native 转发与事件循环的分配收口** `S`
  - 依赖：—
  - 参考：`06-OPT-AUDIT.md §3 P2-11、P2-12`；范式见 `native/core/ssh/channel.cpp` 的 `PendingWriteQueue`
  - 产出：`native/core/fwd/pump.{h,cpp}`、`native/core/io/EventLoop.{h,cpp}`
  - 要点：
    1. `BidirectionalPump` 的 `erase(0, sent)` 改为 head-offset 排空（或 `deque<string>` + 偏移），向 `PendingWriteQueue` 看齐；256 KiB 缓冲上限与背压语义不变。
    2. `EventLoop::run()` 的 `pollDescriptors` 提升为成员，每轮 `clear()` 复用。
       ~~构建 pollfd 时并行存回调，派发时不再二次 `find()`~~ **不做且已在代码里写明理由**：
       回调可能在执行中注销自己或别的 socket，一旦注销就销毁 map 节点；把回调指针存进并行数组，
       派发到一半就可能拿到悬垂指针。现在的「二次查找 + 拷贝 `std::function`」是这条路径上唯一安全的做法。
    3. 唤醒机制（回环 UDP + 50 ms 兜底轮询）不动。
  - 验收：
    - [x] `fwd_test`、`event_loop_test` 全过（含部分写与背压用例；2 条依赖真实网络的集成用例照常跳过）
    - [x] `pwsh scripts/verify.ps1` 全绿（8 步，ctest 315）
  - 验证：ctest；`pwsh scripts/verify.ps1`

- [ ] **O11 `TerminalRenderer` 渲染分配收口** `M`
  - 依赖：O09
  - 参考：`06-OPT-AUDIT.md §3 P1-10`；`01-DESIGN.md §7.3`（SP04 实测纪律）
  - 产出：`src/SshTool.App/Terminal/TerminalRenderer.cs`、`src/SshTool.Core/Terminal/RowRunBuilder.cs`
  - 要点：
    1. `RowRunBuilder` 增加「写入调用方提供的 `List<CellRun>`」重载（保留现有返回新表的重载给测试），渲染器持实例级缓冲，每行 clear + 重填。
    2. `DrawGlyphRun` 不再每 run 新建 `CanvasTextLayout`：预缓存左对齐/居中两种 `CanvasTextFormat`，改用 `DrawText(text, rect, color, format)`；宽字符居中与 fake-bold 偏移的视觉结果必须与现状一致。
    3. **不得退回逐格绘制**——§7.3 SP04 纪律第 1 条（逐格 50–70 µs/格）是硬性红线，run 合并保留。
  - 验收：
    - [ ] `RowRunBuilder` 单测覆盖新重载，输出与旧重载一致
    - [ ] `pwsh scripts/verify.ps1` 全绿
    - [ ] 📱 PerfPage 场景①②③ 的 `draw/s` 不低于优化前、`帧 max` 无新长尾，数据回填 `doc/PERF-REPORT.md §3.1`
  - 验证：`pwsh scripts/verify.ps1`；真机 Q01

- [ ] **O12 修复硬编码文案门禁** `S`
  - 依赖：—
  - 参考：`06-OPT-AUDIT.md §3 P2-13 ①`
  - 产出：`scripts/check-hardcoded-text.ps1`
  - 要点：
    1. 把「含中文的行」从**豁免**改为**命中即报错**（现在的 `$chinesePattern` → `continue` 恰好让这把尺子量不到它要量的东西）。
    2. 扫描范围加上 `.cs` 中对 `.Text/.Title/.Content/.Header/PrimaryButtonText/SecondaryButtonText/PlaceholderText` 的中文字面量赋值；**日志字符串不在范围内**，避免误伤。
    3. 保留 `Views/Debug/`、`obj`、`bin` 豁免；输出按文件分组并给出总数，便于 O13 分批清零。
    4. 改完 `verify.ps1` ④b 会立刻变红——这是预期结果，把当次输出作为 O13 的待清单基线记进进度日志。
  - 验收：
    - [ ] 脚本能报出 `Controls/SessionOverlay.xaml` 与 `Views/Sync/AccountSyncPage.xaml.cs:86` 这类真实命中
    - [ ] 脚本对日志字符串与 `Views/Debug/` 不报警
  - 验证：`pwsh scripts/check-hardcoded-text.ps1`

- [ ] **O13 硬编码文案清零** `M`
  - 依赖：O12
  - 参考：`06-OPT-AUDIT.md §3 P2-13 ②`；`05-CODE-AUDIT-UI-REDESIGN.md §7.3`
  - 产出：非 Debug 的 XAML / `.xaml.cs`、`src/SshTool.App/Strings/{zh-cn,en-us}/Resources.resw`
  - 要点：
    1. 按「Controls + Dialogs」→「Views 主干」→「Views/Sync」三批推进，每批一个提交。
    2. `x:Uid` 用稳定语义名；两份 resw 的 key 集合必须完全一致（`ResourceParityTests` 已有该断言）。
    3. 英文标签更长，改完顺手看一眼按钮宽度与 ContentDialog 的换行。
  - 验收：
    - [ ] `pwsh scripts/check-hardcoded-text.ps1` 零命中
    - [ ] `ResourceParityTests` 全过；`pwsh scripts/verify.ps1` 全绿
    - [ ] 回头把 `05-CODE-AUDIT-UI-REDESIGN.md §10` 的「可见文案全部资源化」重新勾上
  - 验证：`pwsh scripts/verify.ps1`

- [ ] **O14 零散优化项** `S`
  - 依赖：—
  - 参考：`06-OPT-AUDIT.md §3 P2-14`
  - 产出：`src/SshTool.App/Platform/AppEntityLookup.cs`、`src/SshTool.Core/Terminal/PaneTree.cs`、`src/SshTool.Core/Common/BackoffTable.cs`（新增）、`src/SshTool.App/Infrastructure/ColorHex.cs`（新增）
  - 要点：
    1. `AppEntityLookup` 去掉三处 `.Result`（改异步签名或进页面前预取名称缓存）。
    2. `PaneTree.FindLeaf` 改树内直接查找，不再经 `Leaves()` 建表。
    3. 抽 `BackoffTable.DelayFor(int[] steps, int attempt)` 供三处调用——**三张退避表本身按协议各不相同，不要合表**。
    4. 抽 `ColorHex.TryParse`，替换 `ColorSwatchPicker` / `GroupHeader` / `AppearanceListViewModel` 三处逐字重复的解析。
    5. `Properties/Default.rd.xml` 的收窄**不在本任务**：需 ARM Release 实测包体与冷启动，归 Q09。
  - 验收：
    - [ ] 新增 `BackoffTable` / `ColorHex` 的单测；三处/三处调用点全部改完
    - [ ] `pwsh scripts/verify.ps1` 全绿
  - 验证：`pwsh scripts/verify.ps1`

---

## 12. 真机验收待办

> AI 完成任务时把未能验证的 📱 项抄到这里，格式：`- [ ] <任务ID> <验收项原文>`。人工验证后勾选。

- [x] X01 已完成（2026-09-18）
- [x] SP01 ARM Release 真机运行 SpikePage，报告 9 项全 PASS（ulong 上限/固定键序/Unicode/emoji 往返）；失败按 D9 兜底降 UWP 包 5.4.x 重测
  > 2026-09-18：原先**无法执行**——调试页入口是 `#if DEBUG`，而 Release 才启用 .NET Native。已加 `EnableDebugPages` 开关，
  > 用 `-p:Configuration=Release -p:Platform=ARM -p:EnableDebugPages=true` 出包；现成产物：
  > `src/SshTool.App/AppPackages/SshTool.App_0.1.0.0_ARM_Test/`（含 `Dependencies/arm/` 三个依赖包，装机见 `doc/ENV.md` §3）。
- [x] X02 已完成（2026-09-18，app.log 记录 Core/Native 版本号）
- [x] SP02 📱 已完成（2026-09-18，真机 app.log 记录 OpenSSL 3.6.3）
- [x] SP04 📱 FPS 矩阵已完成（2026-09-18，ARM Release/.NET Native，28 组）
- [x] SP04 📱 可用中文回退字体名：**Microsoft YaHei UI**（2026-09-18 真机 CanvasFontSet 枚举，已回写 01-DESIGN §7.4）
- [ ] Q07 📱 开始屏幕各磁贴尺寸（小/中/宽/大）与应用列表图标清晰，深浅开始屏幕都可辨
- [x] SP03 📱 Lumia 经 Wi-Fi 对局域网 SSH 服务器执行成功：697 ms；curve25519-sha256 / ecdsa-sha2-nistp256 / chacha20-poly1305@openssh.com / hmac-sha2-256；指纹与 PC 同一服务器一致（2026-09-18）
- [ ] SP03 📱 回环 UDP 唤醒探测结果（可用/不可用）
- [x] SP05 📱 已完成（2026-09-18，中文+英文两轮）
- [ ] X03 📱/👤 画廊页运行并切换深浅色，颜色/字号/间距/图标符合 02-UI-DESIGN §2（MainPage DEBUG 按钮「X03 Token 画廊」）
- [x] X04 📱 已完成（2026-09-18，真机 app.log 取回核对）
- [ ] X07 📱/👤 画廊页控件区展示 StatusDot 五态（连接中/重连中脉动）、Banner 四 Severity、EmptyState、SectionHeader、LoadingOverlay、TransientToast（1.5s 自动消失）
- [ ] U05 👤/📱 画廊页逐个弹出 7 个对话框返回正确结果；连发两个按顺序显示不崩溃（MainPage DEBUG「X03 Token 画廊」内对话框区）
- [ ] SP06 📱 ExtendedExecution 授予/撤销情况；锁屏 1/5/15 分钟、切应用、省电模式下的 SSH tick 存活时长
- [ ] SP06 📱 DPAPI `LOCAL=user` 1 KiB 往返与重启后解密成功
- [ ] SP06 📱 HTTP GET/HEAD 返回 401 且可读取头/体；带引号的 `If-Match: "revision-0"` 可发送
- [ ] SP06 📱 按实测结论回写 `01-DESIGN.md §10`、D10、D11、R5、R6
- [ ] N05 👤 密码认证集成跑通：用真实账号设 `SSH_TEST_HOST/PORT/USER/PASSWORD` 后跑 `auth_test.exe --gtest_filter='AuthIntegrationTest.Password*'`（成功进 Established + 错密码映射 201 可重试两条）
- [ ] N05 👤 KI 集成跑通：找一台 `keyboard-interactive` 启用的服务器（本机 Windows sshd 与 192.168.1.25 均只提供 `publickey,password`），另设 `SSH_TEST_KI=1` 跑 `AuthIntegrationTest.KeyboardInteractiveRoundTrip`
- [ ] N06 👤 POSIX stty 验收：在一台 POSIX 服务器上授权 `native/tests/fixtures/keys/ed25519_openssh.pub` 后，设 `SSH_TEST_HOST/PORT/USER/PASSWORD` + `SSH_TEST_FIXTURE_KEYS=1` + `SSH_TEST_POSIX=1` 跑 `channel_test.exe` 的 `PtySizeMatchesRequest`/`PtyResizeChangesReportedSize`/`SendEofLetsPeerFinish` 三条（Windows ConPTY 等价验证已通过）
- [ ] N10 👤 x64 Debug 连接 WSL sshd 成功
- [ ] N10 📱 Lumia 连接局域网服务器成功
- [ ] T05 👤 x64 Debug：连接后 `ls --color` 彩色显示正确（MainPage DEBUG「N10 调试连接」，发送框输入）
- [ ] T05 📱 Lumia 上 TerminalView 显示正确
- [ ] T06 👤 x64 Debug：连接后执行 `sh` 粘贴或上传 `tools/term-test/attrs.sh`，各属性显示正确并截图记入进度日志
- [ ] T06 📱 Lumia 上中文与 emoji 对齐正确
- [ ] T07 📱 Lumia 竖/横屏切换后执行 `stty size`，确认远端行列数与显示网格一致
- [ ] T09 📱 英文、退格、回车、中文拼音「你好」均正确送达远端（MainPage DEBUG「N10 调试连接」，点终端区域弹键盘）
- [ ] T10 📱 单手完成 Ctrl+C、Ctrl+Z、Esc、Tab 补全；锁定 Ctrl 连续发送多个控制字符
- [ ] T11 📱（蓝牙键盘或 Continuum）Ctrl+C、Alt+B、方向、F1–F12 正确
- [ ] T12 📱 长按选词、拖柄扩展、复制到系统剪贴板
- [ ] T14 📱 普通屏回滚流畅；`less` 中滑动翻页；捏合不抖
- [ ] T15 📱（Continuum）vim `:set mouse=a` 点击定位、htop 点击、tmux 滚轮正常
- [ ] D03 📱 真机写入后用设备门户下载 LocalFolder，`secrets.bin` 中搜索不到测试密码明文
- [ ] D05 👤 x64 Debug 冷启动进入 MainPage，日志含各阶段耗时
- [ ] D05 📱 ARM Release 冷启动正常
- [ ] U02 📱 100 台主机滚动流畅
- [ ] U06 「删除后再次连接会重新弹 TOFU」在 D06 后回归
- [ ] U07 x64：从主机列表到出现提示符，vim/htop 可用
- [ ] U07 📱 同上，且无白屏跳变；旋转屏幕后布局与 `stty size` 正确
- [ ] U08 📱 断网后看到倒计时，「立即重连」可用
- [ ] U10 📱 开启 tmux 的主机断网重连后回到原 tmux 现场
- [ ] U12 x64 大窗口：4 窗格同时输出互不串扰，拖分隔条后每个窗格 `stty size` 各自正确
- [ ] U12 📱 Continuum 下同上；断开 Continuum 回到手机后会话与布局完好
- [ ] P01 📱 终端页不自动息屏；切走再回来：仍在线或自动重连
- [ ] S05 📱 自检全部通过，记录 Argon2id 解锁耗时
- [ ] P02 📱 Wi-Fi 切蜂窝后 10 s 内会话恢复可用
- [ ] S14 📱 修改主机约 3 秒后自动同步
- [ ] F04 👤 集成：`SSH_TEST_HOST/PORT/USER/PASSWORD` + `SSH_TEST_FWD_HTTP=host:port` 跑 `FwdIntegrationTest.LocalForwardToHttp*` 取回 `HTTP/`；`SSH_TEST_FWD_REMOTE=1` 跑远端回显
- [ ] A03 📱 修改字号/配色后已打开终端即时生效
- [ ] K02 📱 生成 ed25519 → 公钥加入服务器 `authorized_keys` → 用该密钥登录成功
- [ ] U16 👤 x64：在已由桌面端创建保险库的账号上用同步密码解锁成功（本机无桌面端建库账号，待提供后经 LoginPage→登录→VaultUnlockPage 实测）
- [ ] U16 📱 真机创建或解锁成功（注册→建库→RecoveryKeyDialog 勾选保存→首次同步；或对已建库账号用同步密码/恢复密钥解锁）
- [ ] R01 📱 反复进入退出终端 50 次：旧页面可回收、SessionInfo 事件只触发一次、无明显内存增长（fix(R01) d20bf4c 后订阅全部成对可拆，代码结构已支撑）
- [ ] C-03 📱 终端焦点/SIP 回归：打开菜单后 SIP 回弹、切会话、分屏、旋转、SIP 收起/再弹出；菜单/键条/右键三入口打开片段选择器发送或取消后 SIP 自动回弹（fix(C03) 43ffc0a）
- [ ] Phase0 📱 UI 重构前截图基线：360×640、640×360、Continuum 三组（05 文档 Phase 0 出口第 4 步）
- [ ] V01a 📱 画廊 V2 演示区观感：AppPageHeader 三宽态、SegmentTabs（含按压反馈与「已选」播报）、SurfaceCard 四态、StatusPill 四态、IconButtonStyle，深浅两主题；新 MDL2 字形（E700/E76C/E72A/E73E/E8A9 等）在 W10M 15063 字库逐个点亮确认
- [ ] V01b 📱 画廊观感：AppListRow 四态（按压/选中底色对比度）、FormSection 三态、BottomActionBar 两态（SIP 弹出动画期间收起时机）、Banner 各 Severity+重试、EmptyState 三变体默认字形 15063 点亮；200% 文本缩放下 64/48 行高两行文本不裁切（Phase 1 出口综合：深浅/手机/宽屏/缩放四矩阵无裁切）
- [ ] V02 📱 主机页重构回归：快速连接折叠行展开/收起与重启后记忆、页头同步图标旋转/各相位配色/点击手动同步、HostRow 行点按连接+Chevron 编辑+长按菜单 6 动作（桌面右键可达性，⋯按钮已移除）、宽屏页头+PaneToggle 无双标题
- [ ] V02 👤 en-US 下底栏/页签/页头/折叠行文案截断检查
- [ ] V03a 📱 终端页信息条回归：单行紧凑/点击展开双行详情/横屏隐藏地址；菜单项（会话/键条/片段/SFTP/外观/断开/关闭）与重连倒计时 en-US 截断
- [ ] V03b 📱 终端宽屏工作区：键条 keycap 底边+颜色双表达、TabStrip/分屏/手机信息条命令状态共享模型、Workspace 多标签+分屏操作、断线/重连蒙层
- [ ] V04 📱 表单/Settings 回归：Slider 拖动中不重复落盘（C-07 fb057f8 证据为 x64 Debug 构建日志 0 编译错误）、恢复默认按钮、HostEdit 五 Section 与高级折叠、en-US 标签截断
- [ ] V05 📱 SFTP/同步页回归：文件行 AppListRow、传输队列、U17 同步状态 Pivot/开关、U19 冲突解决、U20 安全清理流程（关闭同步密码/修改密码/删除保险库/注销账号）；SyncStatePresenter/ConflictPresenter 已单测（+36）
- [ ] C-07 👤 设置滑块去重验证：x64 Debug 构建日志 0 编译错误（2026-09-21 verify ⑤ 步已覆盖，无需复跑）
- [ ] Q01 📱 性能基准：§15 指标全部达标，或报告中说明差距与原因（真机 Release ARM 实测）
- [ ] Q02 📱 100 次连接/断开循环后内存回落到基线 +10% 以内；4 小时会话无崩溃；挂起/恢复 20 次回归（PerfPage Q02 区脚本，报告 perf-q02-*.txt）
- [ ] Q03 👤 安全自查人工项（doc/SECURITY-REPORT.md §1–§5）：设备门户下载 LocalFolder 搜测试密码/私钥/恢复密钥；app.log 抽查脱敏；热点抓包确认文档密文并记录登录明文风险；删主机后凭据消失；WACK 结果回填
- [ ] Q02 📱 稳定性与泄漏治理：100 次连接/断开循环后内存回落到基线 +10% 以内；4 小时会话无崩溃
- [ ] Q03 👤 安全自查：设备门户下载 LocalFolder 搜索测试密码/私钥片段/恢复密钥；日志抽查；抓包确认文档为密文；WACK 运行结果；OpenSSL/libssh2 CVE 检查
- [ ] Q05 📱 无障碍：讲述人走通「添加主机 → 连接 → 断开」主流程
- [ ] Q08 📱 Continuum 体验打磨：完成一次完整运维流程（2 个标签、分屏、SFTP 下载、片段发送）无布局问题
- [ ] Q09 👤 打包、签名与侧载指南：按 INSTALL.md 在另一台（或恢复出厂设置的）Lumia 上从零安装成功
- [ ] Q10 👤📱 v1.0.0 发布前总检：真机验收待办清空或每条都有明确处置；设计文档与实现一致性复核；版本号 1.0.0.0；Release 包归档
- [ ] F06 📱 远程转发（-R）把手机可达服务暴露到服务器端口并访问成功；动态转发供局域网电脑作 SOCKS 代理访问成功
- [ ] S16 📱 场景 1–8、10 通过（参见 doc/INTEROP-REPORT.md）
- [ ] S16 📱 场景 9（鸿蒙端参与）通过，或记录鸿蒙端侧已知差异（参见 doc/INTEROP-REPORT.md）
- [ ] F07 📱 两级跳板连接成功；断开中间跳后下级进入重连而非挂死
- [ ] O01 📱 PerfPage「Q02 连接/断开 ×100」与长稳复测，内存 Δ 与 `sessions/threads/sockets/screens` 回填 `doc/PERF-REPORT.md §5.1`（验证 `pendingOutput_` 删除后的内存曲线）
- [ ] O03 📱 首页↔终端页往返 50 次，内存无明显增长、主机列表不重复刷新（`05` §10「进入退出终端 50 次无事件倍增」）
- [ ] O06 📱 真机登录 + 一次完整同步周期成功（Wi-Fi 与蜂窝各一次），确认复用连接后同步延迟下降且无握手异常

---

## 13. 进度日志

> 格式：`日期 | 任务ID | commit | 说明 / 遗留 / ⏳ 等待项`

| 日期 | 任务 | commit | 说明 |
|---|---|---|---|
| 2026-09-17 | — | — | 规划文档初版（README、01–04） |
| 2026-09-17 | X01 | af72863 | ⏳ 等待：手机开开发者模式 + WinAppDeployCmd 部署实测（ENV.md §2/§4）。机器侧已盘点入 `doc/ENV.md`；工具链决策：VS2026 构建 C#、VS2017(v141) 构建 Native（VS2026 无 ARM32/v141-UWP），SDK Target 19041；CMake 未安装（X06 前需装）；VS2026 能否部署 W10M 未测 |
| 2026-09-17 | X02 | af72863 | 骨架完成。dotnet test ✓；两段式构建：VS2017 Native ✓ + sln x64 Debug ✓ / ARM Release(.NET Native) ✓。偏差：Tests 不入 sln（sln 级还原会损坏其 assets，见 ENV.md 踩坑）；App 引用 Native 的 winmd 产物而非项目引用；SDK Target 17763→19041（已回写 01-DESIGN §1.2） |
| 2026-09-17 | SP01 | 1351374 | ⏳ 等待：📱 真机跑 SpikePage（ARM 包已含入口，MainPage DEBUG 按钮 → SpikePage）。宿主机侧完成：JsonSpike 9 项检查 + 单测 3 条、SpikePage、6.2.14 维持锁定；任务不勾选 |
| 2026-09-17 | SP02 | 16da4f3 | ⏳ 等待：📱 真机显示 OpenSSL 版本（👤 也可 PC 部署 x64 Debug 确认）。方案 A 成功：vcpkg 3.6.3 四 triplet ✓（ARM 需 triplet 内 8.3 短路径 /LIBPATH 补 19041 um/arm+ucrt/arm）；Native 链 libcrypto+crypt32，`OpenSslVersion()` 上屏；x64 Debug / ARM Release 全链路构建 ✓；D16/R1、ENV.md §7、NATIVE-BUILD §4 已回写；任务不勾选 |
| 2026-09-17 | X03 | 69e4def | ⏳ 等待：👤/📱 运行画廊页确认深浅色切换观感。Token 四字典 + ThemeService（系统强调色覆盖 + ColorValuesChanged）+ 画廊页 + check-magic-numbers.ps1（正负验证 ✓）；x64 Debug / ARM Release 构建 ✓；任务不勾选 |
| 2026-09-17 | X04 | a0c5eff | ⏳ 等待：👤/📱 运行应用确认 app.log 生成与脱敏。SshErrorCode 全码表 + LogRedactor/LogRotationPlanner + FileLogger + 双语 resw；单测 +22（共 25 ✓）；x64 Debug / ARM Release ✓；PRI257 为固有良性告警；任务不勾选 |
| 2026-09-17 | X05 | 4b241fe | 完成（无 📱 项）。双配置包内 appconfig.json 解包校验分别对应；AppConfigParser 单测 6 条（共 31 ✓）；清单补四方向，能力三项与版本号此前已就位 |
| 2026-09-17 | X06 | 1354ad1 | 完成（无 📱 项）。verify.ps1 全绿（含 -Arm ⑥ 步）；失败注入退出 1 并指名步骤②；cmake 免装（回退 vcpkg 缓存）；顺带修掉 VS2026 bundle 增量打包 bug（AppxBundle=Never，ENV.md 踩坑） |
| 2026-09-17 | X07 | 83ffc8e | ⏳ 等待：👤/📱 画廊页控件观感确认。Core Mvvm 四件 + 单测 15（共 46 ✓）；App Infrastructure 六件 + NavigationService 返回链（§4）；Controls 六件全走 Token；verify 全量与 -Arm ✓；任务不勾选 |
| 2026-09-17 | D01 | 7840be6 | 完成（无 📱 项）。模型 10 + Defaults + IdGenerator + 校验器 4；单测 +48（共 94 ✓）；verify 全绿 |
| 2026-09-17 | D02 | 9973bd9 | 完成（无 📱 项）。IFileSystem/JsonStore（原子写/损坏备份/迁移链/LoadWarnings）+ 7 Codec（Extra 往返）+ Repository（ChangeOrigin 事件）+ ConfigService 级联 + UwpFileSystem；ISecretStore/SecretKeys 提前自 D03；踩坑：Newtonsoft 默认 DateParseHandling 把 ISO 字符串变 Date 破往返，统一走 JsonText.ParseObject 禁用；单测 +44（共 138 ✓）；verify 全绿 |
| 2026-09-17 | S01 | 964aaea | 完成（无 📱 项）。SyncConstants 与桌面端 crypto-vault.ts 逐项对齐（AAD 域/SPM1/KDF/上限）；文档模型 7 个含 Clone；ServerSecrets null=键不存在；单测 +7（共 145 ✓） |
| 2026-09-17 | S02 | 17dda97 | 完成（无 📱 项）。Validator/Reader/Writer 严格按 §4.1–4.3（路径化错误、固定键序、序数排序、无 BOM/缩进、2MiB 拒绝、SameContent 忽略 updatedAt）；CanonicalBase64/PrivateKeyFormat 移植；fixtures×2；入站私钥指纹复算（需 native KeyTool）留给 S10；单测 +91（共 236 ✓） |
| 2026-09-17 | S09 | 6087bf0 | 完成（无 📱 项）。SyncMerge 移植 sync-merge.ts（JObject 通用字段合并 + Reader 转回）；冲突无值化；Merge 1–5 + preferences/secrets 部分键/双删用例；单测 +16（共 252 ✓） |
| 2026-09-17 | S06 | 338bea0 | 完成（无 📱 项）。IHttpTransport 抽象 + ApiError/ApiErrorKind（§2.3）+ ITokenStore；ApiClient：Base URL 规范化/端点安全检查/统一头/FormatRevisionEtag/错误解析（注入时钟，Retry-After 秒数与 HTTP-date）/超时与网络映射，21 端点（单次发送，重试刷新归 S07）；DTO 手写解析（未知键忽略，revision 全程字符串）；UwpHttpTransport；ApiClient 用例 1/2/9 + 每 DTO 一条；单测 +62（共 314 ✓）；verify 全绿 |
| 2026-09-17 | S07 | 6f23d6e | 完成（无 📱 项）。§2.4 发送策略：可重试判定与退避（注入 sleep）、401 单飞刷新与重放、刷新失败 uncertain、终端鉴权清会话、HEAD 404 CodeUnknown→GET 回退与元数据校验；抓获并修复单飞槽位竞态（同步完成时 finally 先于赋值）；ApiClient 用例 3–8 + 终端鉴权/HEAD 元数据用例；单测 +18（共 332 ✓）；verify 全绿 |
| 2026-09-17 | T08 | dc9b28a | 完成（无 📱 项）。鸿蒙端仓库不在本机，以 §7.5 为权威并先补文档（~/F 键修饰变体 xterm 惯例）；KeyMap 纯函数（字符键与布局无关、修饰参数、Alt 前缀、Backspace 可配）；StickyModifiers 三态机（OneShot 用后自动释放、Changed 事件）；另修 S07 单飞并发用例的调度竞争（659cbde，确定性时序）；单测 +55（共 387 ✓）；verify 全绿 |
| 2026-09-17 | D04 | 18e456a | 完成（无 📱 项）。§8.3 全表 24 键定义表 + SettingsRepository（EnsureDefaults/非法回退/Changed/24 对访问器）+ LocalSettingsStore（LocalSettings）；shortcuts/hostGroupCollapsed 默认定为 "{}"；T13 未开始故无临时接口需替换；反射双向对拍；单测 +15（共 402 ✓）；verify 全绿 |
| 2026-09-17 | U11 | 3c2cd4d | 完成（无 📱 项）。鸿蒙端 PaneTree.ets 不在本机：按任务要点等价覆盖（结构 16 + 布局/导航 10 + TabSet 12）；PaneTree Split/Close 上提/方向导航/布局/序列化，TabSet 活动标签与快照；抓获 Split 自环 bug（须先取父节点再建分支）；单测 +41（共 443 ✓）；verify 全绿 |
| 2026-09-17 | A01 | 22a16f5 | 完成（无 📱 项）。AppearanceResolver 三级解析链（主机 → 全局默认 → 内置默认，01-DESIGN §8.1 已先补「内置外观代码定义、不写入 appearances.json」注记）；AppearanceService：内置只读、删除回退引用主机为 null、Changed 带受影响主机集合、SetDefaultAsync；单测 +16（共 459 ✓）；verify 全绿 |
| 2026-09-17 | A02 | 7892a27 | 完成（无 📱 项）。BuiltInThemes 9 套（id builtin-<slug>、每次返回克隆、逐套注释配色来源）；鸿蒙端仓库不在本机，Harmony Dark/Light 按 02-UI-DESIGN §3 Token 族推导；D01 Defaults.DefaultAppearance 工厂保留（codec/clone 测试在用）；单测 +6（共 465 ✓）；verify 全绿 |
| 2026-09-18 | — | bcec66b | 修真机两处启动/加载失败：① App 以裸 winmd 引 Native，manifest 缺 `Microsoft.VCLibs.140.00[.Debug]` → 激活 Native 时 0x8007007E（三配置全中，ARM Release 亦然）；② `Grid.ColumnSpacing` 需 contract 5.0 → 15063 真机 XamlParseException 0x802B000A。修法见 ENV.md §5；verify.ps1 步骤⑤ 新增 WMC0151 门禁（注入 `ColumnSpacing` 实测能拦） |
| 2026-09-18 | — | bcec66b | 顺文档全量代码审计。对拍全部通过：§2.1 颜色 19 项、§2.2/2.3 Token 全键、§3.1 SyncConstants 全项、§8.3 设置键 24 项与默认值、PRI 内 ZH-CN/EN-US 实测存在（PRI257 确认良性）。修掉 App 层 5 处：`Window.Current` 线程静态导致的后台线程 NRE（DispatcherHelper、ThemeService.ColorValuesChanged）、激活前阻塞读配置、挂起不刷日志队列、OnLaunched 重入重复注册服务/重复订阅 BackRequested。魔法数字门禁原先只扫 Views/Controls/Dialogs，MainPage.xaml 逃检（已扩为全 App 树，补 Gap*/BorderThin* Token） |
| 2026-09-18 | SP01 | bcec66b | ⏳ 仍等：📱 ARM Release(.NET Native) 真机跑 SpikePage。👤 已在 **PC x64 Debug** 复核 9/9 全 PASS（keyOrder/int/unicode+emoji/bool/null/int 数组/嵌套对象/ulong 上限 18446744073709551615/时间格式）——但该配置 `UseDotNetNativeToolchain=false`，走 CoreCLR，**验不到 SP01 的核心风险（Newtonsoft 在 .NET Native 下的行为，D9）**，故不勾选。并修掉一处使该验收根本无法执行的问题：调试页入口原为 `#if DEBUG`，Release 包里按钮不显示；改为 `DEBUG_PAGES` + csproj `EnableDebugPages` 开关（Debug 默认开，Release 显式传参），已出带入口的 ARM Release 包 |
| 2026-09-18 | SP01 | bcec66b | **完成**。📱 ARM Release(.NET Native) 真机 9/9 全 PASS（用户回报），与 PC x64 Debug(CoreCLR) 逐项一致 → D9 成立：Newtonsoft 12.0.3 只用 JsonTextReader/Writer/JObject 在 .NET Native 下键序、Unicode+emoji、ulong 上限、时间格式均无偏差，`NetCoreUwpVersion` 维持 6.2.14，不降 5.4.x。D9/§1.2/ENV.md §6 已回写。过程中修两处挡路问题：调试页入口 `#if DEBUG` 导致 Release 包进不去（改 `EnableDebugPages` 开关）、SpikePage 报告新增环境行（DeviceFamily/OS build/架构+配置/工具链），避免再出现「这结果跑在哪」的歧义 |
| 2026-09-18 | SP04 | 927c721 | ⏳ 等待：📱 真机跑 RenderSpikePage 记录 FPS 与中文字体名。AI 侧完成：Win2D.uwp **1.26.0** 锁定（min 15063 下构建/打包无告警；ARM Release 包内 Canvas.dll 实测 ARM32 `1C4 machine`；不取 1.28.3 是因 1.27+ 把原生库 RID 从 `win10-arm` 改成 `win-arm`，UWP 工具链未验证）；RenderSpikePage（3 负载 × 2 网格 × 单/双实例，CompositionTarget.Rendering 驱动，脏行模式用逐行 CanvasRenderTarget 局部重绘，CanvasFontSet 枚举中文回退字体，OnNavigatedFrom 里 RemoveFromVisualTree）；scripts/fetch-fonts.ps1 按固定 SHA256 取 JetBrains Mono v2.304；包体 3.4→4.8 MB。另注：Win2D 同样依赖 VCLibs，若无本日修复会同样 0x8007007E |
| 2026-09-18 | SP04 | 5202014 | ⏳ 第一轮真机数字到手（见 ENV.md §8）：**逐格绘制不达标**——48×30 全屏 99–105 ms/帧、88×24 全屏 138–152 ms/帧，对 §15 的 30 fps 预算差 3–5 倍；脏行模式 48×30 单实例 42–53 ✔、88×24 单实例卡线、双实例 18–27 ✘。据此：§7.3 的「行内 run 合并」是必需项而非优化项。本轮改动：① 仪表纠偏——原先只报 CompositionTarget tick/s，而 CanvasControl 跟不上会合并 Invalidate，读数偏乐观，现同时报 draw/s；② 新增「全屏 run 合并」负载，量化 §7.3 真实画法；③ 应用户要求做导出——DebugReport（环境自述行 + 落盘 `LocalState\spike-reports\` + 复制剪贴板 + 写 app.log），SpikePage/RenderSpikePage 共用；④「跑全矩阵并导出」一键跑完 4 负载 × 2 网格 × 单/双实例共 16 组并出报告 |
| 2026-09-18 | SP04 | 03e404e | 报告已能经设备门户自取（`phone-portal.ps1 -Pull`，配对一次即可）。首份全矩阵报告的环境行显示跑的是 **Arm Debug/CoreCLR**，非 .NET Native，数字待 Release 复测。**抓出我自己的两处测试设计错误并改正**：① run-merged 只比 per-cell 快 4%，因为测试内容是每格随机 16 色、平均 run 长≈1，没东西可合并 → 新增内容分布维度（real 约 85% 默认色成段着色 / rand 最坏情况）；② dirty3 用了每行一张 RT + 每帧 N 次 DrawImage，与 §7.3 的「整视图一张 RT、只重画脏行、一次 DrawImage」不符 → 按设计改写。可直接采信的只有 idle：0 draw/s、60 tick/s。另：中文回退实测 Microsoft YaHei UI，01-DESIGN §7.4 已回写，该验收项勾选 |
| 2026-09-18 | SP04 | a5a40ec | **完成**。第三轮真机（ARM Release/.NET Native/v0.1.0.1）28 组矩阵：真实内容下 48×30 逐格 107.5 ms → run 合并 20.6 ms → 行缓存+脏行 11.0 ms；88×24 为 148.6 / 29.2 / 11.8 ms；idle 0 draw/s；双窗格每画布仍 ~30 draw/s。**D5 保持不变、无需兜底**，并新增三条硬性纪律（逐格绘制禁用、run 合并必需、每画布上限 ~30 draw/s，T04 按此设计）。中文回退 Microsoft YaHei UI，cell 9.0×19.0 px。另修两处挡路问题：EnableDebugPages 默认只在 Debug 开 → VS 生成的 Release 包没有调试入口（改为默认全开，Q09 关）；所有构建同为 0.1.0.0 导致旁加载不替换且无法分辨 → bump-version.ps1 + MainPage 显示 v0.1.0.x/配置/工具链。报告全程经 `phone-portal.ps1 -Pull` 自取，未再人工抄数 |
| 2026-09-18 | X04 | d57132b | **完成**。最后一条 📱 验收用设备门户取回真机 `LocalState\logspp.log` 核对：91 行，格式 `2026-09-18 00:40:07.355 [INFO] [AppConfig] …` 与 §12.2 要求逐字符一致；按 `password|token|key|secret|passphrase` 扫描无命中（当前只有启动与配置两类日志，真正的脱敏路径待 S 系列接入后复查）。顺带印证 X05 的包内 appconfig 生效：syncApiBaseUrl 与 logLevel 都读到了 |
| 2026-09-18 | SP05 | d57132b | ⏳ 等待：📱 按页面内 7 步脚本操作并导出。AI 侧完成：InputSpikePage 按 §7.5 搭哨兵原型并全量记录 TextBox/CoreWindow/InputPane 三路事件，每条旁注「按 §7.5 本该发什么」；内置 7 步脚本 + `---- STEP n ----` 分段标记；复用 DebugReport 导出。**WMC0151 门禁当场抓出 `BeforeTextChanging` 是 contract 5.0（1709），15063 没有** → 改 `ApiInformation.IsEventPresent` 守卫（仓库首处 ApiInformation 守卫）。包 bump 到 v0.1.0.2 |
| 2026-09-18 | SP02 | 16e5947 | **完成**。最后两条验收靠真机 `app.log` 自证：MainPage 启动行记录 `v0.1.0.3 | Arm Release | .NET Native | Core: 0.0.1 | Native: 0.0.1 | OpenSSL: OpenSSL 3.6.3 9 Jun 2026`——ARM32 真机上 native 组件加载成功、libcrypto 链接可用且能调用。**N01 的依赖只剩 SP03** |
| 2026-09-18 | X01 | 16e5947 | **完成**。ENV.md §2 由「👤 待回填」改为实测表：Lumia 950 / `Windows.Mobile` / **OS 10.0.15254.603** / ARM32，开发人员模式与设备门户均可用，包全名 `SshTool.LumiaSsh_<ver>_arm__f1yd6nwvtjrr4`，配对与取文件/装包流程见 §3.1。📱「空白应用启动成功」早已被超额满足（四份调试页报告 + app.log）。X02 的真机待办同时勾掉 |
| 2026-09-18 | SP03 | b43982e | ⏳ 等待：👤 提供一台可达的 SSH 服务器 + 📱 真机执行。AI 侧完成：`fetch-third-party.ps1`（libssh2 1.11.1，固定 SHA256，不入库）；**libssh2 零补丁编译链接通过 x64 Debug 与 ARM Release**，排除 agent_win.c/os400qc3.c，后端走 LIBSSH2_OPENSSL + SP02 的 libcrypto；链接期无 AppContainer 禁用 API 报错；`SshSpike` WinRT 类（connect→handshake→userauth_password→exec→读 stdout，报告含各阶段耗时、协商的 KEX/HOSTKEY/CIPHER/MAC 与 hostkey SHA256 指纹）与回环 UDP 唤醒探测；SpikePage 加 SSH 测试区（参数记 LocalSettings，**密码只进内存**）；PATCHES.md 记零补丁结论。踩坑：Native 工程缺 `/utf-8`，GBK 误读中文注释吞行导致诡异编译错（§5 早有记录，已补开关）。包 v0.1.0.4 |
| 2026-09-18 | Q07 | cffad50 | ⏳ 等待：📱 开始屏幕观感确认。AI 侧完成：`tools/assets/{icon.svg,icon-plated.svg,generate.ps1}` —— 图标是「终端提示符 >_」，磁贴走透明底白字形（底色交给系统强调色，深浅开始屏幕都清晰），StoreLogo 等会落在白底的场合用带底板变体（#2F6FE4）。generate.ps1 不依赖外部工具，用 WPF 渲染自写的 SVG 子集，一次产出 52 个 PNG：7 个基名 × 5 档 scale + Square44 的 targetsize 16/24/32/48/256 及其 altform-unplated。清单补 Square71x71/Square310x310 与启动画面底色 #000000；csproj 改用 `Assets\*.png` 通配符。踩坑：.NET 的 `Math.Round` 是银行家舍入，50×125%=62.5 舍成 62，UWP 校验要 63（APPX1619），改用 AwayFromZero。包 v0.1.0.5 |
| 2026-09-18 | — | （事故） | **本文件被我写空过一次**：回填哈希用了 `open(p,"w").write(open(p).read())`——`open(p,"w")` 先截断，内层再读到的就是空文件。302a5c8 提交了空文件，b43982e 又带了一次。已从 16e5947 恢复并补回丢失内容。教训：先整体读入变量、再打开写；改文档后要核对行数 |
| 2026-09-18 | SP05 | 217646e | **完成**。中文/英文两轮真机采集，§7.5 五条提交规则定稿，**300 ms 静默兜底不启用**（TextComposition 三件套可靠）。四个实测要点：① 英文键盘直通无组合事件，中文 IME 下连英文字母也进组合态；② **退格必须走 KeyDown(Back)**——连按 3 次时 KeyDown 三次齐全，而哨兵只 2 字符可吃，TextChanged 只报得出 2×DEL，第 3 次丢失；③ **AcceleratorKeyActivated 的 Character 事件里 VirtualKey 是字符码**（l=108→Separator、s=115→F4、-=45→Insert），判键只能用 KeyDown.VirtualKey 或 CharacterReceived.KeyCode；④ Enter 收到两次 KeyDown（RepeatCount 0/1），不去重会双发 
。另修采集页真 bug：CompositionEnded 原先只取「提交文本」，会把组合中那次退格整个吞掉，改为与非组合态同一套完整差分 |
| 2026-09-18 | U05 | 271a33d | ⏳ 等待：👤/📱 画廊页观感与结果确认。AI 侧完成：Dialogs/ 七对话框（HostKey/HostKeyMismatch/Credential/Passphrase/KbdInteractive/Confirm/ExitWithSessions），各自 `static ShowAsync` 经 DialogService 排队、结果类型化、None 映射取消、PasswordBox 读出即清空；Mismatch 次按钮需输入主机名确认（deferral 阻止关闭）；ConfirmDialog 通用化（isDanger 时主按钮 AppDangerBrush，覆盖 ConfirmDelete 场景）；Controls/ 新增 MonoText（等宽可选中，Compact 切 Caption 字号）与 RandomArtView；Tokens 补 `AppMonoFontFamily`/`GapXsTop`/`GapSmTop`，Controls.xaml 补 `MonoTextStyle`；画廊页加对话框演示区（结果脱敏：密码只显长度）+ 连发两个验证排队；verify 全绿（465 单测、魔法数字、x64 Debug 无 WMC0151）；任务不勾选 |
| 2026-09-18 | SP03 | a3c139c | ⏳ 等待：📱 真机两项（ARM 包 v0.1.0.6 已备好，手机接 USB 后我自动装包+推种子+拉报告）。AI 侧：**PC x64 Debug 验收①通过**（👤 提供测试服务器 192.168.1.25；无人值守实测 `uname -a` 返回 Linux sun 7.0.0-30-generic Ubuntu 24.04 x86_64，exit=0；协商 kex curve25519-sha256 / hostkey ecdsa-sha2-nistp256 / cipher chacha20-poly1305 / mac hmac-sha2-256，总耗时 652ms，报告 sp03-ssh-20260918-084849.txt；PC 回环 UDP 探测亦可用）。**新增无人值守机制**：启动检测 `LocalState\ssh-autotest.json`，读后即删（密码不留盘），自动跑 SSH+UDP 并出报告——N 系列回归不用再戳屏幕；`phone-portal.ps1` 加 `-Push`。踩坑（记 ENV §5）：未签名 appx PC 拒装 0x800B0100、CurrentUser TrustedPeople 仍 0x800B0109、msbuild 无 Deploy 目标，最终「解压 appx + Add-AppxPackage -Register」免签名解决；appx 签名证书 EKU 必须是 1.3.6.1.5.5.7.3.3（311.10.3.4 是 EFS）。verify 全绿；任务不勾选 |
| 2026-09-18 | SP03 | 8162b9e | **代码任务完成**。用户回报 Lumia 真机 Wi-Fi SSH 成功：697 ms，协商算法与 PC 完全一致，同一服务器指纹一致；UDP 留入渐进真机待办。修正设备门户路径必须含 `\LocalState`，种子改推应用自建 `spike-reports` 目录以继承 ACL；自动测试增加并发防重。按用户要求改为“自动门禁完成即继续编码，📱/👤 按里程碑渐进回归”，并据此归档 X03/X07/U05。465 个 Core 测试、native ctest、文档和 XAML 门禁通过；宿主 UWP 源码编译成功，打包阶段遇本机 SDK `GenerateAppxPackageRecipe`/`MrmSupportLibrary` 工具异常，独立跟踪，不转成人工验收。 |
| 2026-09-18 | SP06 | 8616af2 | **代码任务完成，真机渐进回归**。新增 PlatformSpikePage：ExtendedExecution 请求/撤销日志、DisplayRequest 成对管理、DPAPI `LOCAL=user` 1 KiB 往返与跨重启文件、Windows.Web.Http GET/HEAD/If-Match、1–30 分钟 SSH tick 后台存活测试；结果统一落 `spike-reports` 与 app.log，密码不保存。UWP C#/XAML 编译通过；人工场景已登记待办，不阻塞 N01/P01。 |
| 2026-09-18 | N01 | 4e78eb6 | **完成**。固定 URL/SHA256 拉取 libssh2 1.11.1、libvterm 0.3.3 release tar、Argon2 20190702；`NativeCore.vcxitems` 统一 UWP 原生清单，Argon2 固定 ref.c + `ARGON2_NO_THREADS`；宿主链接静态 OpenSSL 并通过 libssh2/libvterm/RFC 9106 三条冒烟。VS2017 v141 的 x64/Win32/ARM 三架构构建成功，`verify.ps1 -Arm` 全绿。顺带定位并修复 Appx 打包 `MrmSupportLibrary.GetLocation` 空引用：受控终端缺进程级 `PROCESSOR_ARCHITECTURE`，门禁从 Machine 级补回。 |
| 2026-09-18 | N02 | 0673ba6 | **完成**。EventLoop 以 WSAPoll + 经 100 ms 自检的回环 UDP socket 对唤醒；AppContainer 不允许时自动退化为 50 ms 有界轮询；定时器最小堆、跨线程 FIFO Post、socket 注册/修改/移除与 SessionThread 幂等启停齐备；WinsockInit 进程级引用计数，I/O 线程命名。移植并 Windows 等价替换为 11 条 I/O 测试，含 UDP 收发/所有权、轮询兜底与 1000 次会话创建启停后 `GetProcessHandleCount` 零增长；native 共 15 测试，`verify.ps1 -Quick` 与 `-Arm` 全绿。 |
| 2026-09-18 | N03 | 1cc2d0d | **完成**。新增 libssh2 非阻塞会话生命周期：`WSAEWOULDBLOCK` 后由 WSAPoll 可写事件配合 `SO_ERROR` 完成连接，连接/握手/关闭定时器、合法迁移表、远端断开与优雅关闭齐备；修复 connect 后立即 close 的入队竞态。新增 8 条会话测试（默认 7 通过、环境集成 1 跳过），native 共 23 条；临时注入环境变量对既有测试服务器 `192.168.1.25:22` 实际握手到 Authenticating 并关闭，32 ms 通过，未发送认证信息；`verify.ps1 -Arm` 全绿（465 Core、x64 Debug、ARM Release/.NET Native）。libssh2 源与本项目同名 `session` 对象冲突，已用独立对象目录隔离。 |
| 2026-09-18 | N04 | 1438c45 | **完成**。新增 `native/core/ssh/hostkey.{h,cpp}`：`SHA256:`+base64 无填充指纹、MD5 对照指纹、OpenSSH 逐字节对齐的 Drunken Bishop randomart（17×9）、blob 内嵌算法名解析（libssh2 枚举兜底）、三态比对（Ok/Unknown/Mismatch，提取失败 fail-closed 归 Mismatch）。SshSession 握手成功后、进 Authenticating 前同步调 `hostKeyCallback`（空回调=TOFU 放行）；Reject 或提取失败 → 错误 303、`SSH_DISCONNECT_HOST_KEY_NOT_VERIFIABLE` 优雅断开走 Closing→Closed，被拒后 `hostKeyInfo()` 仍可取供对比视图。黄金向量：固定 ed25519 blob 的 SHA256/MD5/randomart 与 `ssh-keygen -l`/`-E md5 -l`/`-lv` 输出逐字符写死比对 ✓；对测试服务器 192.168.1.25 实测三条集成用例全过（默认放行取指纹 ecdsa-sha2-nistp256、回调比对通过进 Authenticating、伪造指纹被拒且状态序列无 Authenticating）。native 共 35 测试；`verify.ps1 -Quick` 全绿；v141 UWP x64 Debug / ARM Release 构建 ✓（NativeCore.vcxitems 的 Core 组为此补 `/std:c++17`——v141 默认 C++14 无 `std::optional`）。 |
| 2026-09-18 | N05 | c6a269e | **代码完成**。`native/core/ssh/auth.{h,cpp}`：非阻塞驱动 `authenticatePassword`/`authenticatePublicKey`（frommemory）/`authenticateKeyboardInteractive`/`queryAuthMethods`，受理即复制并 `OPENSSL_cleanse` 清零调用方凭据缓冲，AuthOp 析构兜底清零；失败停留 Authenticating 可重试（authMaxAttempts=3），耗尽或 authTimeoutMs 超时进 Error；错误映射 201/202/203/204（含 libssh2 1.11.1 三条吞错路径的归一化）。KI 按本项目规格走 `IAuthPromptSink` + `AuthPromptGate` 条件变量阻塞等待（默认 120 s 超时视为取消，超时/不死锁有单测）。fixtures：ed25519 OpenSSH 未加密/加密 + RSA-3072 PEM 未加密/加密（短语 `n05-test-passphrase`）。单测 15 条全过（native 共 50）；实测：本机起私有 Windows sshd（127.0.0.1:2222，临时 authorized_keys，测后已回收）连通公钥未加密/加密两条——加密私钥错短语精确映射 204 后重试正确短语进 Established；192.168.1.25 方式探测 `publickey,password`。顺带修 N03 潜伏 bug：`updateSocketInterest` 在 libssh2 无阻塞方向时挂了 Readable\|Writable，WSAPoll 水平触发下空转，改为只挂 Readable。密码成功/KI 两条集成待真实凭据与 KI 服务器（已登记待办）；`verify.ps1 -Quick` 全绿，v141 x64 Debug / ARM Release 构建 ✓。 |
| 2026-09-18 | N06 | a6affcf | **代码完成**。`native/core/ssh/channel.{h,cpp}`：SshChannel 绑定 SshSession，三种打开形态（openShell / exec / execWithPty）各走 open_session→request_pty→process_startup 三段 EAGAIN 续跑流水线；`setenv` 小队列在 startup 前先行、拒收仅记日志不致命；写路径任意线程受理入队、4 MiB 背压上限整次拒收，`PendingWriteQueue` 纯逻辑组件（head offset 分块 FIFO）；读路径双流（stdout/stderr）交替抽干防窗口饿死，`classifyChannelRead` 纯函数分 Data/Eof/Stalled/Error；resize 在途合并后到优先；sendEof 排在待发数据之后；close 握手→exit-status/exit-signal→free→onClose（PeerEof/ExitStatus/ExitSignal/Error），onOpen/onClose 各恰好一次；另提供阻塞一次性 `execCommand`（测试/诊断用，超时 15 s + 2 s 关闭宽限）。session 接线：通道注册表、socket 事件 driveChannels、releaseResources 先 notifyChannelsSessionLost 再 session_free、Established 态常驻 Readable + 通道出站停滞才挂 Writable。离线单测 9 条（迁移表穷举、受理拒绝、队列部分写/EAGAIN 停滞/清零、读分类四分支）；实测本机私有 sshd（测后已回收）：exit 42 退出码、execCommand 往返、stdout/stderr 分流三条过；ConPTY 探针（SSH_TEST_CONPTY 门控）：execWithPty 113x37 实测生效、shell resize 后 80x24→113x37——**resize 验收等价通过**；POSIX stty/cat 三条用例已入测试（SSH_TEST_POSIX 门控）待 POSIX 服务器（已登记待办）。native 共 67 测试；`verify.ps1 -Quick` 全绿；v141 UWP x64 Debug / ARM Release 构建 ✓。 |
| 2026-09-18 | N07 | d4ef709 | **代码完成**。纯逻辑件 `keepalive.h`（`keepaliveInboundObserved` 双信号取或：Readable 事件 / FIONREAD 待读字节增长；`KeepaliveMissTracker` 连续静默周期计数，maxMisses=0 只发不判；`KeepaliveProbe` 开窗/基线/到期裁决）与 `reconnect_policy.h`（`BackoffSchedule` 默认 1/2/5/10/20/30 s、上限 6 次、0=无限、空序列回落默认、越界钳末档）。session 集成：错误码 404 `KeepaliveTimeout`；`setKeepaliveConfig` 仅 Idle 受理；进 Established 时 `armKeepalive`（want_reply=1，首周期宽限 + FIONREAD 基线），每拍观测入站→发送→按 libssh2 `seconds_to_next` 预约下一拍，连续静默达标或发送失败 → Disconnected(404)；`probeNow` 仅 Established 受理，压 interval 到 libssh2 下限逼出真发（刚发过则沿用旧基线保留在途应答证据），开 5 s 判定窗口复用 keepaliveTimer_ 槽位，无入站即 404 断线、有入站则 miss 清零续周期链；`isAutoReconnectable`：Disconnected 恒可重连，Error 按链路类/凭据类分。测试 27 条（native 共 94）：纯逻辑 22（含 KeepaliveProbe 5）+ 受理语义 2 + 集成 3；实测私有 sshd（测后已回收）：1 s 周期 2 拍发送计数增长且 0 miss 不误判、interval=0 完全静默、probeNow 2 s 窗口判活不断线。踩坑：sshd_config 的 Windows 路径单反斜杠会被转义吞掉（`\a`、`\n`），须用 `sshd -T` 校验解析结果。`verify.ps1 -Quick` 全绿；v141 UWP x64 Debug / ARM Release 构建 ✓。 |
| 2026-09-18 | N08 | 11b7518 | **完成**。`native/core/ssh/error_codes.h`：kSshErrorCode* 常量表 23 码（命名镜像 C# 成员名 kSshErrorCode<CsMember>，注释带中文文案摘要），`toSshErrorCode(SshSessionError)` 穷尽 switch 无 default（新增枚举 MSVC C4061 点名），`toSshErrorCodeFromLibssh2(int)` 通用诊断兜底映射（超时 402 / 认证 201·202·204 / 协商 301 / 主机密钥 303 / socket 401·403，未识别 → 999；上下文精细映射仍在 session.cpp/auth.cpp 各自路径）。`check-error-codes.ps1` 正式化：逐项比对「成员名=数值」（不再是仅数值集合），多/缺/异值逐项列出并退出 1；正负向实测：23 码一致通过，注入 404→440 报「数值不一致: KeepaliveTimeout」退出 1。单测 8 条（穷尽性、数值锚定 C# 表、libssh2 四分类映射、toString 覆盖，native 共 102）。`verify.ps1 -Quick` 全绿；v141 UWP x64 Debug / ARM Release 构建 ✓。 |
| 2026-09-19 | N09a | 334ff0a | **完成**。`src/SshTool.Native/Bridge/`：`SshSession`（每会话一条 `SessionThread`；`ConnectAsync`/`AuthenticatePasswordAsync`/`AuthenticatePublicKeyAsync`/`AuthenticateKeyboardInteractiveAsync`/`OpenShellAsync`/`ExecAsync` 全走 `create_async` 返回 `IAsyncOperation<int>`/`<ExecResult^>`，错误码即 N08 统一码）+ `ConnectOptions`/`HostKeyInfo`/`HostKeyCheckEventArgs`/`AuthPromptEventArgs`/`StateChangedEventArgs`/`ExecResult`。HostKeyCheck：0 订阅 TOFU 直通，否则 60 s `DecisionGate` 决策窗（Deferral 挂起暂停计时、末个 Complete 重启整窗；超时/取消 fail-closed 303）；AuthPrompt 由 core 120 s 硬窗口兜底（不因 Deferral 暂停），0 订阅立即空答复；`ContentDirty` 合并投递 + `FetchPendingOutput` 过渡取数（T03 接管后移除）；Close 幂等、析构 Shutdown 安全停线程、core 回调持 `Platform::WeakReference` 防循环。纯逻辑抽 `native/core/bridge_logic/`（`DirtyCoalescer`/`DecisionGate`），单测 12 条（native 共 114）。踩坑：v141 `/ZW` 不收 `!T()` 终结器（C3941，C# 侧须 using/Dispose）；`create_async` 不收 mutable lambda；自定义事件存储须委托私有平凡事件（add 返回其 `+=` token），native 容器持句柄与 `Map<int64,Handler^>` 方案均让位于此；vcxproj 全工程补 `/std:c++17`。§6.2 已回写全部偏差。`verify.ps1 -Arm` 全绿；x86/x64 Debug、ARM Release 构建 ✓。 |
| 2026-09-19 | N09b | a79cb1d | **完成**。Core：`ISshSession`/`ISshSessionFactory`/`SshConnectRequest`/`HostKeyCheck`/`AuthPrompt`/`SessionStateKind`/`SshExecResult`；`ITerminalScreen` 先定义（T03 实现，N09b `Screen` 恒 null）。App：`NativeSshSession` 适配 WinRT 类型与 `IAsyncOperation`→`Task`，始终订阅 native `HostKeyCheck`（Core 无编排 fail-closed Reject + Deferral 挂起 60 s 窗；`AuthPrompt` 无编排立即 Cancel），`NativeSshSessionFactory` 待 N10/D05 注入。`FakeSshSession` 可脚本化（预设返回、`Fire*` 触发事件、调用记录）+ 工厂；自身单测 15 条（Core 共 480）。`verify.ps1 -Arm` 全绿；x64 Debug / ARM Release 构建通过。 |
| 2026-09-19 | N10 | e595531 | **代码完成**。`DebugConnectPage`：主机/端口/用户/密码 → `NativeSshSession` 连接；HostKeyCheck 把指纹/randomart 打到页面后自动 Accept；密码认证后 `ExecAsync("uname -a; whoami")`；事件日志带相对耗时；密码不落盘不记日志。MainPage `DEBUG_PAGES` 入口。`verify.ps1` 全绿。⏳ x64 Debug 连 WSL sshd、📱 Lumia 连局域网服务器（已登记真机待办）。 |
| 2026-09-19 | T01 | ad72466 | **完成**。移植鸿蒙 `CellGrid`/`VtermBridge`：16 字节格、脏行、revision、resize 保留交集；默认色标记 `0x00000001`/`0x00000002`；bit8 invisible、bit9 软换行；VT 语料（SGR/光标/滚动区/alt-screen/DECSET）+ 宽字符续格；跟踪 DECCKM/2004/1006（libvterm 无对应 termprop，从输入扫描）。sb_* 空操作留给 T02。native 166；`verify.ps1 -Quick` 全绿。§7.1 属性表补 bit9。 |
| 2026-09-19 | T02 | 3f145f1 | **完成**。`ScrollbackBuffer` 预分配定长环形缓冲（默认 5000、上限 50000）；`copyWindow`/`copyWindowWithCols`（TOCTOU）/`copyFromBottom`；VtermBridge sb_* 接入；100000 行 `storageBytes` 不变；弹回默认色标记往返。native 190；`verify.ps1 -Quick` 全绿。 |
| 2026-09-19 | T03 | 0aded15 | **完成**。`snapshot` 纯函数 CopyDirtyRows/CopyViewport/GetText；WinRT `TerminalScreen` + `NativeTerminalScreen`；shell 输出喂 vterm，Resize 同步网格与 PTY。DebugConnectPage 改 OpenShell + GetText。snapshot 单测 7（native 197）；`verify.ps1` 全绿。 |
| 2026-09-19 | T04 | 6eb4ced | **完成**。`FrameSchedulerCore`：可见集合、Wake、空闲 30 帧退订、530 ms 闪烁（注入时钟）；异常隔离。App `FrameScheduler` 走 `CompositionTarget.Rendering`，Wake 任意线程经 DispatcherHelper 封送。Core 单测 8（共 488）；`verify.ps1` 全绿。 |
| 2026-09-19 | T05 | 1a93249 | **代码完成**。`TerminalCell`/`CellBufferReader` 小端 16B 解析；`RowRunBuilder` 属性变化拆段、宽字符单独成段、行尾默认空白丢弃。`TerminalRenderer` 一张 RT 脏行重绘 + 块光标（默认色标记换成 Token 前景/背景）。`TerminalView` 挂 FrameScheduler；DebugConnectPage 改交互 shell + 发送框。Core 单测 +10（共 498）；`verify.ps1` 全绿。⏳ x64 `ls --color`、📱 Lumia 显示（已登记真机待办）。 |
| 2026-09-19 | T06 | 6f67534 | **代码完成**。`TerminalPalette`：标记值→外观色、bold-as-bright 反查 0–7→8–15、reverse 互换、dim 向背景混 50%（§7.3 由「降 alpha」改为与鸿蒙 `applyDim` 一致）。渲染器：粗体字重或 1px 描边、italic/underline/strike/invisible、宽字符 2 格居中；光标 block 反色 / bar / underline + 530 ms 闪烁，失焦空心框；`CreateResources`/`DeviceLost` 重建行缓存。`tools/term-test/attrs.sh`。Core 单测 +12（共 510）；`verify.ps1` 全绿。⏳ x64 attrs.sh 截图、📱 中文/emoji 对齐（已登记真机待办）。 |
| 2026-09-19 | T07 | cfb4163 | **代码完成**。`GridSizeCalculator` 按可视宽高扣除双边 padding 与覆盖式键条，向下取整并保证 20×5；`FontMetrics` 用 Win2D 测 `M`，按行高系数取整，字号/DPI 变化重测；`TerminalView.Session` 接线并对尺寸变化做 100 ms 防抖 `Resize`。Calculator 单测 +10（Core 共 520）；`verify.ps1 -Arm` 全绿（native 197、x64 Debug、ARM Release/.NET Native）。⏳ Lumia 旋转后 `stty size`（已登记真机待办）。 |
| 2026-09-19 | T09 | cabcf6b | **代码完成**。`SentinelDiff` 对两个 U+200B 做新增/删除差分（有新增不把哨兵缺失当远端退格）；`SoftKeyboardInput` 按 SP05 五条规则：焦点在哨兵只走 TextBox、非组合态 TextChanged 发新增、组合结束走完整差分、Enter 去重发 `\r`、非组合 Backspace 走 KeyDown。点击终端 `Focus(Programmatic)` 弹 SIP；`InputPane.Showing` 每次读 OccludedRect 并 `EnsuredFocusedElementInView`，与视图相交高度并入网格计算。TerminalView 暴露 `Input` 并写入 `ISshSession.Write`。Core 单测 +12（共 532）；`verify.ps1` 全绿。⏳ 英文/退格/回车/拼音「你好」真机送达（已登记真机待办）。 |
| 2026-09-19 | T10 | 6536c80 | **代码完成**。`KeyBarLayout` 解析逗号布局（未知忽略、去重、空串回退默认，与设置键默认值对拍）；`KeyBar` 横向滚动、修饰键三态（强调色/锁定底+2epx 底边）、方向键 400/60 ms 连发、动作事件；右侧固定 hidekb。`Haptics` 10 ms，`VibrationDevice` + ApiInformation 守卫。调试连接页接入同一 `StickyModifiers`。Core 单测 +13（共 545）；`verify.ps1` 全绿。⏳ 单手 Ctrl+C/Z、Esc、Tab、锁定 Ctrl（已登记真机待办）。 |
| 2026-09-19 | T11 | 84a081b | **代码完成**。`ShortcutMap` 默认 §5.16（Ctrl+Shift 复制/新标签等，不抢 Ctrl+C）；JSON 对象覆盖、未知键忽略、非法 JSON 回退默认、冲突检测先命中先赢。`HardwareKeyboardInput`：哨兵聚焦时不抢可打印/回车/退格；Alt 走 AcceleratorKey（不用 Character 事件的 VirtualKey）；快捷键先于 KeyMap。有物理键盘时点终端隐藏 SIP 并把焦点留在 TerminalView。Core 单测 +9（共 554）；`verify.ps1` 全绿。⏳ 蓝牙/Continuum Ctrl+C、Alt+B、方向、F1–F12（已登记真机待办）。 |
| 2026-09-19 | T12 | 21a4d0e | **代码完成**。`SelectionModel` 选词（字母数字+`-_./~`）、选行、全选、宽字符边界、反向拖动、软换行不插换行、行尾空白裁剪；回滚区用绝对行。`SelectionLayer` 高亮/拖柄/工具条；`ClipboardService` 写系统剪贴板。复制轻震 + TransientToast「已复制」。Core 单测 +12（共 566）；`verify.ps1` 全绿。⏳ 长按选词/拖柄/剪贴板（已登记真机待办）。 |
| 2026-09-19 | T13 | edfb682 | **完成**（无 📱 项）。`PasteProcessor` 把 CRLF/LF/CR 归一为 `\r`，可选 `ESC[200~…ESC[201~]` 包裹，4 KiB UTF-8 分块不切断多字节。多行且 `PasteConfirmMultiline` 时弹确认框（前 5 行预览，「不再提示」关掉确认）。键条 paste 走同一路径。Core 单测 +7（共 573）；`verify.ps1` 全绿。 |
| 2026-09-19 | T14 | 0e1855a | **代码完成**。`ScrollController` 偏移 0=底部，夹取 [0, scrollback]；底部时新输出跟随，回滚中保持偏移；用户输入回底。`MouseEncoder` 滚轮 SGR/X10；alt-screen 拖动按 `altScreenScroll` 发方向键或滚轮（未开鼠标上报时滚轮回退方向键）。`PointerInput` 平移+惯性+捏合 8–28 与字号气泡；回滚中 `CopyViewport` 且不画光标。Core 单测 +10（共 583）；`verify.ps1` 全绿。⏳ 回滚/`less`/捏合（已登记真机待办）。 |
| 2026-09-19 | T15 | f3199f9 | **代码完成**。MouseEncoder 四种模式×SGR/默认；SGR 坐标 300 不截断、X10 夹到 223。鼠标左键：上报或本地选择（Shift 强制、双击选词、三击选行）；滚轮在上报开启时发序列。右键复制/粘贴/全选/清屏/片段；悬停 IBeam。Core 单测 +10（共 593）；`verify.ps1` 全绿。⏳ Continuum vim/htop/tmux（已登记真机待办）。 |
| 2026-09-19 | D03 | 9464eb2 | **代码完成**。InMemorySecretStore + SecretKeys 前缀删除；ISecureFile / InMemorySecureFile；DpapiSecureFile `LOCAL=user` 原子写 `secure/secrets.bin`；DpapiSecretStore 串行 JSON 键值表。Core 单测 +5（共 598）；`verify.ps1` 全绿。⏳ secrets.bin 无明文（已登记真机待办）。 |
| 2026-09-19 | D05 | b87c415 | **代码完成**。`AppServices` 组合根：配置/设置/七仓库/DPAPI 凭据/ConfigService；启动各阶段耗时写 app.log；挂起 Flush；未处理异常记类型名；LoadWarnings 上 MainPage Banner。`verify.ps1` 全绿（598）。⏳ x64/ARM 冷启动（已登记待办）。 |
| 2026-09-19 | U01 | 680f734 | **完成**。`Views/MainPage` Pivot 主机/会话/隧道 EmptyState；底部 CommandBar 新建/搜索/同步 + 更多进 PlaceholderPage（「将在 Mx 提供」）；返回键规则 7（非主机 Pivot 切回主机）；状态栏随主题；DEBUG 页改到溢出菜单。`verify.ps1` 全绿（598）。 |
| 2026-09-19 | U02 | 08d2467 | **代码完成**。HostListBuilder 分组/name+recent 排序/搜索/折叠 JSON；QuickConnectParser `user@host[:port]` 含 `[::1]:22`；NullHostStatusProvider；HostsPivot + HostRow + 删除确认隧道数；Debug「生成 100 台测试主机」。Core 单测 +35（共 633）。⏳ 100 台滚动（已登记真机待办）。 |
| 2026-09-19 | U03a | 9d87d4c | **完成**。HostEditPage Pivot 连接/认证占位/终端/高级；HostEditState 脏检查；JumpChainValidator 自环/成环/深度 5；环境变量行编辑、初始命令、仅本机标注；新建/编辑/复制；保存失败跳 Pivot 聚焦。Core 单测 +17（共 650）。 |
| 2026-09-19 | U03b | 50fe573 | **完成**。CredentialDraft 保存勾选/取消、切换认证清理；SecretStore 写入/删除；密码框「已保存」不回显；导入/生成进 M7 占位。Core 单测 +11（共 661）。 |
| 2026-09-19 | U04 | 9e5fdb1 | **完成**。GroupManagePage 新建/重命名/24 色板+Hex/上移下移/删除（提示主机与隧道数，ConfigService 清 groupId，仓库层已单测）；HostEdit「管理分组」。verify 全绿（661）。 |
| 2026-09-19 | U06 | 52f0ec1 | **代码完成**。KnownHostsPage 列表/搜索/指纹+randomart 详情/删除确认。MainPage 更多→已知主机。⏳ 删除后再次连接 TOFU 待 D06 回归（已登记待办）。verify 全绿（661）。 |
| 2026-09-19 | D06 | b81535f | **完成**。SessionManager 按 §9.2 编排 TOFU/KnownHost/同步指纹、密码重试 3 次/KI、退避重连、CloseAll、迟到事件丢弃；实现 IHostStatusProvider。Core 单测 +19（共 680）。 |
| 2026-09-19 | D07 | 11523ed | **完成**。ConnectionTester 草稿测连、阶段失败文案键、15s 超时/取消、不开 shell；HostEdit 测试连接。Core 单测 +7（共 687）。 |
| 2026-09-19 | U07 | 9e25ef9 | **代码完成**。TerminalPage 信息条/TerminalView/KeyBar；UwpHostKey/CredentialPrompter；StatusBar 隐藏恢复；主机列表进会话。⏳ x64/📱 提示符与旋转（已登记待办）。 |
| 2026-09-19 | U08 | 09694a6 | **代码完成**。OverlayStateDeriver 全状态组合；SessionOverlay 淡入按钮接 SessionManager。⏳ 断网倒计时（已登记待办）。Core 单测 +9（共 696）。 |
| 2026-09-19 | U09 | 6e9d60f | **完成**（无 📱 项）。接手上一会话留下的半成品（文件已建但未进 csproj、未接线）：SessionSnapshotStore 写/读 `state/sessions.json`（现存主机过滤、损坏按空）；SessionsPaneViewModel 经 ServiceRegistry 共享给会话 Pivot 与终端页 SplitView 侧栏；关闭/切换/新建；冷启动恢复卡片 + [全部恢复]（首个导航、其余后台开）；MainPage 返回退出时有活跃会话弹确认（确认后 CloseAll 再 Exit）。修半成品 bug：关闭按钮 Grid.Column 越界、EmptyState 未分行、Reload 不通知 UI、退出确认结果被忽略、RestoreAll 原实现会连开 N 个 TerminalPage（改为只导航一次）。另把同批未提交的真机联调改动单独入库（5b5df0d：debug_log.h UWP 走 OutputDebugString、JumpSessionId 空值兜底、包 0.1.0.10）。Core 单测 +3（共 699）；`verify.ps1` 全绿。 |
| 2026-09-19 | U10 | d1a9058 | **代码完成**。`AutoRun` 纯函数：tmux 附着在前（名只留 `[A-Za-z0-9_.-]` 其余换 `_`，空名 `main`）、初始命令逐条在后（空行跳过）；SessionManager 每次（含重连）`OpenShell` 成功后逐条加 `\r` 发送，日志只记条数。环境变量经 `SshConnectRequest.Env` 传 native（D06 已接线，本次只补测试覆盖）。⏳ 📱 tmux 断网重连回现场（已登记待办）。Core 单测 +14（共 716）；`verify.ps1` 全绿。 |
| 2026-09-19 | U12 | 905bd3d | **代码完成**。TerminalWorkspace 按 PaneLayout 绝对定位+拖动改 ratio（释放后防抖 Resize）、焦点边框、窄屏聚焦叶树不销毁、不可见窗格退订渲染；TabStrip 新建/关闭/右键菜单；WorkspaceViewModel 多标签经 ServiceRegistry 单例；MainPage AdaptiveTrigger≥720 主从、TerminalPage 宽屏键条默认隐藏。⏳ x64 四窗格/stty 与 📱 Continuum（已登记待办）。Core 754；`verify.ps1` 全绿（含 -Arm）。 |
| 2026-09-19 | S08 | a2635ab | **完成**。AuthStore 基于 ISecureFile（secure/auth.bin，手写 JObject，损坏按未登录）实现 ITokenStore；AuthService 注册/登录防重复、登出失败也清本地、改密成功清本地、拒绝撤销本机；UwpDeviceDescriptorProvider（FriendlyName/平台/包版本，ApiInformation 守卫）。Core 单测 +26（共 754，含持久化/uncertain/重复登录/撤销本机/改密）；`verify.ps1` 全绿。 |
| 2026-09-19 | S10 | 36f0661 | **完成**。SyncLocalAdapter：Build 按 preferences 输出 secrets（NullPrivateKeyInspector 预留 S15）、悬空引用过滤、Writer 零偏差门禁；Apply 指纹变化与运行中隧道两道保护（整份拒）、🏠字段保留、凭据出现覆盖缺失保留、删除级联、ChangeOrigin.Sync。Core 单测 +12（共 754，覆盖 7 项验收）；`verify.ps1 -Quick` 全绿。 |
| 2026-09-19 | U13 | b8e9ec3 | **完成**。SnippetTemplate 纯函数（内置 ${host}/${user}/${port}/${name}、其余按序收集、`$$` 转义、缺值为空、内置优先）+ 发送整形（换行归一 `\r`、SendEnter 补 `\r`，经 ISshSession.Write）；SnippetsPage（搜索+分组）/SnippetEditPage（分组联想+变量说明+脏确认）/SnippetPickerFlyout/SnippetVariableDialog；终端菜单与键条 snippets 接入。Core 单测 +15（共 769）；`verify.ps1` 全绿。 |
| 2026-09-19 | S03 | fc87d52 | **完成**。native AAD/保险库：KDF 入参+范围校验、解密前 hash 常量时间比对、规范 Base64、恢复密钥校验段大小写不敏感、HKDF 手工实现、OPENSSL_cleanse 清零；黄金向量 5 值命中、恢复密钥 10000 次往返+61 点篡改全拒、篡改/非法 KDF 全拒。native 218（+21）；v141 x64/Win32/ARM Release 构建 ✓；`verify.ps1 -Quick` 全绿。遗留：§3.5 kCiphertextHash 与冻结头不一致（c74d… vs 7642…），S03 范围外未改，需后续回改。 |
| 2026-09-19 | Q07 | 0d904f5 | **代码完成**。核对 52 PNG 逐个尺寸正确（重跑字节一致）；补上 cffad50 漏合的清单项：Square71x71/Square310x310、SplashScreen BackgroundColor #000000（=深色 AppBgBrush）；generate.ps1 -List 计数 45→52。⏳ 📱 磁贴观感（已在待办）。`verify.ps1` 全绿，无 APPX1619/WMC0151。 |
| 2026-09-19 | P01 | 11ea517 | **代码完成**。KeepAwakePolicy 三参纯函数（108 格全组合 25 条）+ DisplayRequest 计数保护；BackgroundPolicy 纯状态机 29 条（EE 授予/拒绝/撤销、回前台、计时、会话数 → 请求/断开405/重连/探测/释放）；SessionManager SuspendAllForPolicy/ResumeSuspended/ProbeAll（405 保留面孔原地重连）；LifecycleService 接四事件。⏳ 📱 常亮与切后台（已登记待办）。Core 853；`verify.ps1 -Interop` 全绿。 |
| 2026-09-19 | S04 | 59d93c8 | **完成**。tools/sync-vectors（hash-wasm 4.12.0 锁定，固定 salt/nonce/密码/明文，独立原语生成 + tsx 只读自检桌面端解密）；3 文档夹具经桌面端 zod 校验；JSON+C++ 头双输出；verify.ps1 -Interop 步骤⑦。Core +4（共 853）；native +3（共 221）；generate --check 与重写 zod 全过。遗留：§3.5 kCiphertextHash 旧值偏差（见 S03）。 |
| 2026-09-19 | S05 | 0e5d216 | **代码完成**。WinRT VaultCrypto 桥（后台线程，失败 null）+ IVaultCrypto/NativeVaultCrypto + FakeVaultCrypto（可逆模拟+密码错误）+ 调试页保险库自检（含 Argon2 耗时与桌面向量）。Core +20（共 853）；`verify.ps1 -Arm` 7 步全绿。⏳ 📱 自检与耗时（已登记待办）。 |
| 2026-09-19 | P02 | de65e93 | **代码完成**。NetworkChangeDetector（适配器 id+连接级别判据，断网记 Unknown 不踢会话，防抖 1000ms）+ NetworkMonitor（1s 沿防抖）+ SessionManager.OnNetworkChanged（退避立即重连/已连接 ProbeNow）+ EnergySaverWatcher 与终端页 Banner（双语 resw）。Core 单测 +15（共 906，Detector 9 + Network 6）；`verify.ps1 -Interop` 全绿。⏳ 📱 切网恢复（已登记待办）。 |
| 2026-09-19 | U14 | e3e9e66 | **完成**。SettingsPage 五 Pivot（外观链占位，A05 补）+ KeyBarLayoutEditorPage（两列+上下移+恢复默认即写）+ ShortcutEditorPage（16 动作录制+冲突横幅+恢复默认）+ SettingsViewModel（24 键直连即时生效：主题/强调色/日志/常亮直驱，其余经 Changed）。手测清单：通用三档主题/强调色/排序/快连卡/触感/语言重启生效；终端字号回滚/alt/粘贴新会话生效；键盘布局与快捷键录制冲突恢复；连接常亮后台重连超时轮询；关于版本日志导出清空诊断复制。`verify.ps1` 全绿。 |
| 2026-09-19 | S11 | 521c2b4 | **完成**。VaultCache（S02 读写器编解码，用户绑定，损坏错版重置）+ Pending 双事务日志 + SyncCoordinator 第一部分（Initialize/注册登录防重复/ProbeVault/SetupVault 先落盘幂等/Unlock 双通道/Lock/Delete/SetPreferences 拒关敏感/Logout 失败清本地/改密注销/StateChanged）。§10.1 Coordinator 1–5（38 条：Cache 15 + Coordinator 23，含丢响应重启复用材料）。Core 906；`verify.ps1 -Interop` 全绿。 |
| 2026-09-19 | S12a | 3924c8f | **完成**。SyncNow 单飞 + PerformSync ①–④ 非冲突路径 + Upload（先落盘 pending、确定性错误清除、generation 竞争 + 250ms 复同步）+ CommitRemote；冲突分支占位抛 NotSupported（S12b 接）。修单飞槽竞态（finally 先于赋值，token 守卫清槽）。§10.1 Coordinator 6/9/20 + 上传期改动复同步（15 条）。Core 953；`verify.ps1 -Quick` 全绿。 |
| 2026-09-19 | A03 | 8d9c1e3 | **代码完成**。SampleScreenBuilder（36×8 样例屏）+ HsvColor/Comparer + StaticTerminalScreen；AppearanceList/EditPage（实时预览<100ms、内置复制、脏确认、设默认）+ ColorSwatchPicker 完整版（HSV+新旧对比）；TerminalView.ApplyAppearance（不重连）+ 刷新链路（Changed 按主机重应用）；HostEdit/设置/菜单接入。Core +32（共 953，样例屏验收已勾）；`verify.ps1` 全绿。⏳ 📱 即时生效与观感（已登记待办）。 |
| 2026-09-19 | K01 | fbd5444 | **完成**。keytool 生成 ed25519（手写 openssh-key-v1）/RSA3072/4096 + 解析三格式 + 导出公钥行 + SHA256 指纹；WinRT KeyTool/KeyInfo 桥；14 夹具 + 12 用例（指纹硬编码=`ssh-keygen -lf`、libssh2 加载校验）。修 Bridge/obj 大小写覆盖（Core 组 IntDir 隔离）与 ECDSA/PEM 空行 bug。native 233；v141 x64/ARM 构建 ✓；`verify.ps1 -Quick` 全绿。遗留：KeyInfo 多 Comment 属性未回写 §6.2（K02 可补）。 |
| 2026-09-19 | S12b | a7e65ff | **完成**。冲突三分支（initial-import/remote-deletion/merge-conflict）+ SaveConflict 落盘（摘要不含值）+ ResolveConflict 双策略；重启恢复（相位重建 idle，冲突数据完整）。§10.1 Coordinator 7/14/15/16/17/18/21（12 条）。Core 1008；`verify.ps1 -Quick` 全绿。 |
| 2026-09-19 | A04 | c063fd7 | **完成**。itermcolors（XDocument，DTD 忽略）+ Windows Terminal（单 scheme/数组，多选对话框）+ ThemeImportResult（Id 已分配可直接 Add）；6 真实主题全 20 色位断言 + 非法用例。Core +30（共 1008）。 |
| 2026-09-19 | K02 | be93216 | **代码完成**。KeyImportService（256KiB/去重/短语/落库双写/生成复检）+ IKeyTool/NativeKeyTool + Keys/Detail 页（改名/复制分享/导出二次确认/引用/删除保护）+ Import/Generate 对话框；HostEdit 占位转真流程。Core +13（共 1008）；`verify.ps1` 全绿。⏳ 📱 密钥登录（已登记待办）。 |
| 2026-09-19 | S13 | 3665eae | **完成**。RotateVaultKey（新 keyVersion 建料→偏好先行→重加密→POST vault/rotate→版本校验→落基线，失败回滚+ambiguous 文案）+ Restore/Clear/List 透传 + HandleSyncError（终端鉴权/auth_error、无会话/signed_out、offline 退避 1/2/5/10/30/60/300s Retry-After 优先经 ITimerFactory）+ MarkDirty 3000ms 防抖 + Dispose。§10.1 Coordinator 8/10/11/12/13/19（21 条）。Core 1044；`verify.ps1` 全绿。 |
| 2026-09-19 | A05 | bc1d6f0 | **完成**。ThemeService 运行时补强（CurrentMode+后台线程封送+关强调色回退主题字典色+EffectiveThemeKey）+ StatusBarService.RefreshTheme 收拢 + 设置页说明/Toast；终端配色隔离（只走 Appearance）。手测清单：三档主题即时变色/强调色开关四组合/跟随系统同步/终端不受影响。`verify.ps1` 全绿。另修关强调色残留系统色真 bug。 |
| 2026-09-19 | K03 | b5db43d | **完成**。native SshAgent（惰性滑动超时、清零、快照）+ session authenticateAgent + Bridge SshAgent（keyId 传递，私钥不出 native）+ SessionManager authType=agent（超时对齐/依次试钥/204-lock/无钥弹框/KI 回退）+ LockAgentKeys（挂起清除）+ 设置 Agent 保留滑杆。移植单元 11/11 + SessionManagerAgentTests 16。native 247；Core 1044；`verify.ps1` 全绿。 |
| 2026-09-19 | F01 | 83c37d8 | **完成**。SftpSession 绑已认证会话（open/listDir/stat/lstat/readlink/mkdir/rename/unlink/rmdir/setstat/文件读写 seek/close，I/O 线程纪律+EAGAIN 100ms 切片+取消）；transfer 32KiB 分块、偏移续传、取消标志。6xx 码表 601–606 四处同步（§6.3/C#/resw/error_codes.h，对拍 29 码过）。单测 16 + 集成 4 实测通过（临时 WSL sshd，50MB SHA256 一致 73s、8MiB 续传一致；测后回收）。native 268；`verify.ps1 -Interop` 全绿。 |
| 2026-09-19 | S14 | 7f21fec | **代码完成**。SyncTriggers（User 源主机/分组/隧道+host:/key: 凭据→MarkDirty；启动/回前台>30s/网络恢复/前台轮询/手动；后台停轮询；永不抛）+ ISyncTriggerTarget/SyncIconMap（UI §5.1）/SecretChangedEventArgs；ISecretStore Changed 事件（下行传 Sync 不标脏）；组合根装配同步栈 + MainPage 同步图标旋转绑定。单测 36（Triggers 26 + IconMap 10，验收已勾）。Core 1119；`verify.ps1 -Interop` 全绿。⏳ 📱 3 秒自动同步（已登记待办）。 |
| 2026-09-19 | S15 | 88aa642 | **完成**。IPrivateKeyInspector 移新文件并异步化 + PrivateKeySyncCodec（出站三元组/入站复算）+ KeyToolPrivateKeyInspector（文案对桌面端 decodeSyncedPrivateKey）+ 11 夹具指纹向量（ssh2@1.17，PKCS#8 不支持故出站跳过记警告）+ U17 最小启用点（数据层 SetPreferences）。等价用例 24 + 向量 12 + 开关 2（验收已勾）。Core 1119；`verify.ps1 -Interop` 全绿（含 private-keys --check）。 |
| 2026-09-19 | F04 | d0316c1 | **代码完成**。pump 双向泵（背压/半关闭/统计）+ direct_tcpip 状态机 + local_listener（I/O 线程 accept burst）+ remote_listen（forward_accept 轮询）；session 接线（open_state/fwdLstn_state 串行化门）。pump 单测 7 + 状态机 4 + 准入 2 + 回环 1MB + 门控集成 2（未跑，👤 已登记）。native 283；`verify.ps1 -Quick` 全绿。 |
| 2026-09-19 | F02 | 310436a | **完成**。WinRT SftpSession 桥（会话 I/O 线程串行、SftpCancel consume-once、Shutdown 先拆 SFTP）+ Core Sftp 六件（RemotePath/PermissionBits/RemoteEntry/ISftpClient/TransferItem 速率滑动窗/TransferQueue 单泵）+ NativeSftpClient/StorageFileStreams（IRandomAccessStream 分块）。Core 单测 111（含 FakeSftpClient 供 F03）；`verify.ps1` 全绿。 |
| 2026-09-19 | U15 | f7db8d3 | **完成**。ApiErrorCatalog（§2.3 全 36 码）+ LoginFormValidator + LoginViewModel/LoginPage（登录注册切换、HTTP Banner、密码≥10+确认、邀请码、三端说明、RateLimited 填秒）；resw +64 键双语一致；Api_<CODE> 覆盖单测。真服务器实测：注册 200→登录 200→GET me 200→登出 200（密码只在内存未落日志；测试账号 u15verify20260920024647@example.com 已登出留服务器，可经注销流程清理）。Core 1163；`verify.ps1` 全绿。 |
| 2026-09-20 | U16 | 5293799 | **代码任务完成**。Core：RecoveryKeyInput（格式/校验段复算逐字对齐桌面端 decodeRecoveryKey，SHA256 经 System.Security.Cryptography.Algorithms 4.3.0，正文段大小写敏感不动、前缀/校验段归一）+ VaultFormValidator（同步密码≥8+确认、解锁判空）；单测 48 条含桌面向量 SPM1-YGFi…-0F62… 冻结值对拍、3 行展示切分与 resw 双语覆盖。App：VaultSetupPage/VaultUnlockPage（分段解锁、等宽恢复密钥框、LoadingOverlay「正在生成密钥/正在解锁/正在同步」，文案随 Coordinator syncing 相位驱动）+ RecoveryKeyDialog（等宽 3 行、复制、必须勾选「已保存」，返回键关闭会重弹并警告）+ VaultErrorText（ApiError→Api_ resw）+ SyncNavigation.GoAfterAuth（按 vault missing/locked 路由建库/解锁页，导航后剪返回栈失效中间页，修 U15 返回键弹回登录页循环）+ LoginPage 接线；resw +34 键双语一致；csproj 登记（Core 增 System.Security.Cryptography.Algorithms 4.3.0）。`verify.ps1` 六步全绿（Core 1364、native 299、x64 Debug 无 WMC0151）。⏳ 👤 桌面端建库账号解锁、📱 真机建库/解锁（已登记真机待办）。 |
| 2026-09-20 | Phase0 基线 | 1a1ab79…080de29 | **完成**。05 审查文档入库；脏工作区（U16 完成态 + F03/F05/Q01 开发中 + MainPage 移入 Views/）整理为可复现提交 5293799，基线 verify 全绿。 |
| 2026-09-20 | R01 | 072eee9…d20bf4c | **完成**（05 工作包：终端订阅释放与导航取消）。C-01：TerminalPage/TerminalViewModel 匿名订阅改命名处理器，AttachNative 幂等、Detach/UnbindSession 成对，全部拆除经 Track 登记按订阅反序执行。C-02：Core 新增 NavigationLifetime（Begin/End/IsCurrent/Token/Track，幂等反序、Begin 隐含 End、Track-after-End 立即执行，单测 11 条），8 个 async void OnNavigatedTo 页面全部薄壳化 + 每个 await 后世代检查 + OnNavigatedFrom 先 End；KeyDetailPage 补 DataTransferManager 长寿订阅解除；新增 AppLog（只记异常类型名，脱敏）。规格+质量两轮评审及复核通过。已知：页面 await 期间返回不取消后台连接（需动 SessionManager API，留待后续）；TerminalWorkspace 的同型订阅/fire-and-forget 归 V03。⏳ 📱 50 次进出回归（已登记待办）。 |
| 2026-09-20 | C-03 | 16ce518…43ffc0a | **完成**（05 P0：终端控件抢软键盘焦点）。新建 TerminalIconButtonStyle（AllowFocusOnInteraction=False、MinWidth=TouchTargetMin、全 token），终端页更多按钮/TabStrip 新建与动态关闭按钮/Workspace 分屏关闭按钮全部套用；无障碍名 resw 双语 +5 key；菜单与片段选择器关闭后按「开前采样 SIP + 世代守卫 + 物理键盘早退」恢复哨兵焦点，不无条件拉起 SIP；菜单→片段路径透传采样修复评审发现的恒 false 缺陷。规格+质量三轮评审通过。⏳ 📱 SIP/菜单/旋转/片段回弹回归（已登记待办）。 |
| 2026-09-20 | V01a | 5757f6f…ffcec16 | **完成**（05 Phase 1 前半：Design System V2 首批）。Token 收敛：深浅色板注释归档五层角色（Canvas/Surface1/Surface2/Stroke/Semantic），新 token PadCard/PadXs/PadPill/RadiusPill/SegmentHeight/DisabledOpacity/ExtraWideBreakpoint 均有消费方，动效标准注释成文。MDL2 统一：☰⧉▸▾↪⌨✕✓ 7 处 + KeyBarLayout ⌨ Label 残留全清，新键 IconGlobalNav/IconSplit/IconChevronRight/IconForward/IconCheck（两处码位经 MDL2 官方表与本地渲染核对纠正任务单笔误）；补 AppIconFontFamily。新组件：AppPageHeader（两级标题/返回/双槽/三宽态）、SegmentTabs（均分列/按压反馈/「已选」无障碍后缀）、SurfaceCard（四态）、StatusPill（四 Kind 小点不饱和）、IconButtonStyle（通用，区别于终端专用样式）。画廊 V2 演示区；resw 268 键双语一致。规格+质量评审及复核通过。⏳ 📱 画廊观感与字形点亮（已登记待办）。 |
| 2026-09-20 | V01b | fd906de…1cd17c4 | **完成**（05 Phase 1 收尾：§5.3 组件清单闭合）。新组件：AppListRow（图标/标题/副标题/状态/尾操作五槽、四态、整行可点+尾槽 IsWithin 溯源防误触、SetName(Title) 无障碍）、FormSection（标题/说明/内容/错误固定顺序、normal/error/readonly）、BottomActionBar（主操作+溢出 Flyout、InputPane 感知 SIP 收起让位、Loaded/Unloaded 成对）。既有对齐零行为变化：Banner 补 ActionClick（先于 ActionCommand）、EmptyState 补 Kind 三变体（显式 Glyph 优先）。Tokens 补 BorderThinTop/PadActionBar。画廊补全 11 组件演示；resw 293 键双语一致。规格+质量评审通过（readonly 语义等 4 条以注释澄清；Tapped 路由注释按官方文档落字，未采纳评审相反前提）。⏳ 📱 画廊观感/缩放/SIP 时机（已登记待办）。 |
| 2026-09-21 | V02 | 056dae7 | **完成**（05 Phase 2：Main/Hosts 信息架构重构）。MainPage 顶行 AppPageHeader(Primary)+同步图标按钮入页头（可点手动同步，旋转动画保留，VM 契约零改动）；底栏主命令=快速连接+新建，搜索按钮移除，Debug 9 项迁入新建 DevToolsPage（AppListRow 入口、DEBUG_PAGES 门控、csproj 登记）。HostsPivot 搜索常驻；QuickConnectCard 折叠为可展开行，新设置键 hostQuickConnectExpanded 持久化（D04 表驱动全链路+测试计数 24→25，§8.3 表已补行）。HostRow 基于 AppListRow 重构：行点按=连接（两次点击出口达成）、Chevron=编辑、长按/右键菜单 6 动作与 6 事件契约不变。宽屏去双标题。resw 双语 parity 一致。规格评审 9/9 ✅ + 质量评审 With fixes（唯一 Important=§8.3 漏行已补）。遗留 Minor（评审记录在案，后续打磨）：QuickConnectRow 手写布局可换 AppListRow、徽章槽全隐藏时仍占左边距、DevToolsPage 懒建 VM 不退订（PerfPage 同款）、_ignoreNextTap 右键滞留（旧行为携带）、ToggleSearchCommand/SearchCommand/IsSearchOpen 死契约待清理（拟并 V06）。⏳ 📱/👱 真机回归（已登记待办）。 |
| 2026-09-21 | R03 | 230eed2 | **完成**（05 Phase 3 代码债：AsyncCommand/Forget 统一策略，治理 C-04/C-05）。Core 新增 TaskExtensions.Forget(context, logger) 唯一观察点（4 单测）；ObservableObject.RaiseOnUi 封送失败改丢弃通知（1 单测）；AppLog 补 Logger 访问器。终端/Main 模块 14 文件迁移：TerminalWorkspace（外观「最后一次请求生效」世代门控、分屏/打开主机 Forget、关键 catch 补日志）、TerminalPage/TabStrip/SessionsPivot/HostsPivot/HostListViewModel（Forget + RefreshAsync 世代门控）、MainPage/SessionsPaneViewModel/LifecycleService。verify 全绿（1380 Core + 299 native）。⏳ 📱 真机回归（已登记待办）。 |
| 2026-09-21 | V03a | c4055d8 | **完成**（05 Phase 3 前半：终端页紧凑信息条 + SIP/viewport 确认 + transient 层评估）。InfoBar 改单行（Token TerminalCompactBarHeight=44），点击切换 Normal/Expanded VisualState（Expanded=64 双行详情），WideState 横屏隐藏次要地址；VM InfoCollapsed→InfoExpanded 重命名。终端菜单项 7 + 重连倒计时 1 走 resw 双语（+9 键）。SIP/viewport 已确认正确无需改；transient 层现有布局已满足无需统一。verify 全绿。 |
| 2026-09-21 | V03b | 4f95e48 | **完成**（05 Phase 3 后半：KeyBar Locked 底边 accent 条 + C-06 清理 + 命令共享举证）。KeyBar 键 Border 内叠 Grid+底部 accent 条（Token KeyBarAccentBarHeight=2, AppAccentBrush），Locked 态显示/Off-Active 隐藏。C-06 清理 TerminalView 右键菜单+Toast、TabStrip 菜单+对话框、Workspace 分屏菜单+空态（+13 组双语键）。命令/状态共享举证：TabStrip 与 Workspace 经同一 WorkspaceVM。verify 全绿。 |
| 2026-09-21 | V04a | fb057f8 | **完成**（05 Phase 4 前半：SettingsPage C-07 + §6.4 分组 + C-06）。C-07 修复：Slider 拖动中仅更新显示，ManipulationCompleted 立即提交 VM，键盘/点击 150ms debounce。§6.4：终端/连接页 Slider 用 FormSection 分组（+恢复默认按钮），VM 新增 Default* 常量。C-06：+85 组双语键。verify 全绿。 |
| 2026-09-21 | V04b | dab8f86 | **完成**（05 Phase 4 后半：HostEdit/Keys/Snippets §6.4 + C-06）。HostEdit→五 FormSection + 高级折叠 + BottomActionBar。Keys 行改 AppListRow + BottomActionBar。Snippets 发送改 IconForward+resw。C-06：+77 组双语键。verify 全绿。遗留：HostEditVM.DescribeCredentialState() 中文待 V06。 |
| 2026-09-21 | V05a | 34140bd | **完成**（05 Phase 5 前半：SFTP §6.5 + SftpViewModel C-05 迁移）。文件行改 AppListRow；C-05：Refresh/Upload/NewFolder→AsyncCommand，行命令+8 处 RefreshAsync→`.Forget`；保留 `_refreshSeq`。verify 全绿。 |
| 2026-09-21 | V05b | 4fa3562 | **完成**（05 Phase 5 后半：U17~U20 同步页 + Presenter + 单测）。新建 6 同步页 + 6 VM。Core 新增 SyncStatePresenter + ConflictPresenter，+36 单测(1416)。C-06：+117 组双语键。verify 全绿。 |
| 2026-09-21 | V06 | ef4a817…9aa9b8e | **完成**（05 Phase 6 发布质量：PRI257 清零 + Q04 自动化 + C-06 清理 + Q06 关于/许可页 + 验证修复）。PRI257/PRI175 根因=csproj 显式 PRIResource + UWP Strings\<lang>\ 自动检测双重包含；移除显式声明后 PRI257 清零。Q04：ResourceParityTests(4 单测) + check-hardcoded-text.ps1(豁免 Views/Debug) + 集成 verify ④b 步。C-06：清理 8 页面+2 对话框(+77 键)。Q06：新建 AboutPage/LicensesPage + OPEN_SOURCE_LICENSES.md + PRIVACY.md。验证修复：Sync 页 FormSection.SectionContent 包裹(WMC0035)、GroupHeader→TextBlock(WMC0011)、SftpPage Handled(CS1061)。verify 全绿(1420 Core + 299 native + 7 步全通过，PRI257=0，EXIT=0)。人工项(Q01/Q02/Q03/Q05/Q08/Q09/Q10)已登记真机验收待办。 |
| 2026-09-21 | F05 | b2c151f | **完成**。NativeForwarder（ITunnelRuntime 原生实现，独占 NativeSshSession + Bridge.Forwarder，线程安全字典，TOFU/known_hosts 保存，链路断开 Dropped 上抛）+ AppServices 接线（TunnelManager 单例注册 + ApplyConfig + 仓库 Changed 联动 + StartAutoStartAsync）。verify 7 步全绿（Core 1420 + native 299）。 |
| 2026-09-21 | F06 | bc1a33a | **完成**。TunnelsPivot（列表/启停/每秒速率统计/空态/全部停止）+ TunnelRow（基于 AppListRow/状态点/开关）+ TunnelEditPage（三 Section 结构/路径图示/动态显隐校验）+ LoopbackNoticeDialog（回环隔离提示+不再提示）+ HostEditPage 隧道列表与新建跳转接线；ViewModel 状态/编辑/列表三件套；resw 双语 42 组键。verify 7 步全绿（Core 1420 + native 299）。 |
| 2026-09-21 | S16 | d08a697 | **完成**。产出 doc/INTEROP-REPORT.md，依据 §10.3 规范化十大互通场景测试矩阵、前置条件、操作步骤与预期结果；自动化向量与密码学单测全部就绪，真机联调场景已登记待办。verify 7 步全绿。 |
| 2026-09-21 | F07 | 457ff70 | **完成**。jump_transport（IJumpChannel/Fake/DirectTcpip/BufferedPipe + LIBSSH2 SEND/RECV 回调）+ connectJump（loopback 唤醒对 + 50ms 轮询兜底防 DirectTcpip 通道无 wakeup 挂死）+ Bridge ConnectJumpAsync（上级 I/O 线程开 direct-tcpip 再包 JumpTransport）+ JumpChainPlanner（顺序/环/深度 5，9 单测）+ SessionManager 链式连接（每跳独立认证与主机密钥提示、标题注明 [i/n]、密钥按跳板 host:port 落库、失败级联清理、重连整链重建）。修 session.h 自足性（SSIZE_T/SOCKET）。Core 1430（+9）+ native 306（+7）；verify 7 步全绿。⏳ 📱 两级跳板与断中间跳重连（已登记待办）。 |
| 2026-09-21 | Q01 | f184236 | **代码任务完成**。PerfPage 六场景（⓪静止/①base64/②yes/③vim/④100主机滚动/⑤4×5000回滚）+ FPS 双口径叠加（tick/s、draw/s）+ MemoryManager 守卫读数 + 报告落盘 spike-reports\perf-*.txt，随 Phase0 基线入库；本次逐项核验页面引用的 token 与 API（FeedBytes/ResizeGrid/ScrollbackCount/FrameSchedulerCore.IsRunning/HostsPivot.Attach/DebugReport.*）均存在，入口 DevToolsPage（DEBUG_PAGES 默认开，Release 可用），修正 PERF-REPORT.md 入口路径描述。操作指南见 doc/PERF-REPORT.md §2。⏳ 📱 Release ARM 实测回填 §3/§4 并按结论做针对性优化（已登记待办）。verify 7 步全绿。 |
| 2026-09-21 | Q02 | af10e7b | **代码任务完成**。native diag_counters 四生命周期计数（SshSession/SessionThread/EventLoop sockets/TerminalScreen，Release 可读，析构扣除残留保证可回归）+ NativeInfo.DiagCounters() 导出 + PerfPage Q02 区（「连接/断开×100」每 10 轮采样+GC 静置 Δ% 判定、「长稳 4h」60s 采样+开 shell 挂页面终端渲染+1s 停止响应、挂起/恢复自动计数随报告落盘 perf-q02-*.txt）；EnsureTerminal 幂等重绑。口径与操作指南 doc/PERF-REPORT.md §5。⏳ 📱 100 次循环内存 ≤ 基线+10%、4h 无崩溃、挂起/恢复 20 次（已登记待办；发现泄漏时在此任务追加修复提交）。verify 7 步全绿（Core 1430 + native 306）。 |
| 2026-09-21 | Q03 | ef735ed | **完成**。产出 doc/SECURITY-REPORT.md：§1–§5 逐项给结论与人工操作指南（LocalFolder 搜索/日志抽查/抓包/删主机凭据级联/WACK），代码层证据齐（LogRedactor+单测、DpapiSecureFile LOCAL=user、ConfigService 级联删凭据+单测、登录明文 Banner/SyncApiBaseUrl 配置）；§6 CVE 检查完成——**发现 libssh2 1.11.1 四个高危 CVE（CVE-2026-55200 CVSS 9.2 认证前可触发 PoC 公开等），上游无修复版** → 登记新任务 Q11（cherry-pick 修复 + OpenSSL 3.6.3→3.6.4），Q10 依赖加 Q11，verify 文档计数 111→112。⏳ 👤 §1–§5 人工项已登记待办。 |
| 2026-09-21 | M9 立项 | bdcc765 | **优化审查落档**。三层逐文件核查产出 `doc/06-OPT-AUDIT.md`：确认 2 条内存泄漏路径（`SshSession::pendingOutput_` 无人消费却按字节无上限累积、`SessionsPivot` 与 6 个页面级 VM 订阅应用级单例后从不解除）、1 处门禁失效（`check-hardcoded-text.ps1` 把含中文的行整行豁免，导致 Q04 量不到自己要量的东西；实测 XAML 137 处 + C# 37 处中文字面量未资源化），以及同步路径重复全文档序列化、`Repository<T>` 线性查找、`UwpHttpTransport` 每请求新建 filter/client、vterm 每 feed 重复 CSI 解析、`TerminalRenderer` 每 run 新建 `CanvasTextLayout` 等 14 项。新增 §11 M9 里程碑与 O01–O14 任务，verify 文档计数 112→126。同时订正 `05-CODE-AUDIT-UI-REDESIGN.md`：§10 四条与实测不符的勾选改回 `[ ]` 并注明证据行号，§3 增复核说明（C-01 已修、C-02 落地为整型世代号而非 CTS、C-07 只完成页面侧去抖）。本次不改产品代码。 |
| 2026-09-21 | O01 | 45184d2 | **完成**。删 `pendingOutput_`/`outputMutex_`/`FetchPendingOutput()`（Native + ISshSession + NativeSshSession + Fake + 单测）——该缓冲把每个会话的远端 stdout 逐字节累积，T03 接管渲染后唯一消费者已无生产调用方，shell 存活期内单调增长（cat 1 MB 即 +1 MB 常驻）。连带 `markConsumed()` 也只在该 API 里调用，删除前合并标志已永久为真、`ContentDirty` 每会话只触发一次。`DirtyCoalescer` 改为自复位的 16 ms 时间窗（`markDirty(nowMs)`/`forcePost(nowMs)`，lock-free CAS，时钟注入；通道关闭走 forcePost 保证最后一批输出落屏）。**并补上 `01-DESIGN §4.3` 时序里写了却从未接上的一步**：`TerminalView` 订阅 `ContentDirty` → `FrameScheduler.Wake()`——缺它则终端失焦后帧调度器约 500 ms 退订（30 帧空闲阈值先于 530 ms 闪烁到期），新到的远端输出无人唤醒。`bridge_logic_test` 3 条 consume-rearm 用例→ 6 条时间窗用例（含「无消费者仍持续投递」的回归，旧实现在此只投一次）。§4.2/§6.2 已回写。verify 7 步全绿（Core 1429、native 309）。⏳ 📱 Q02 内存曲线复测（已登记待办）。另注：本次构建仍见既有 `PRI257` 警告（与本任务无关，未动资源/csproj；`05` §10 该条本就未勾）。 |
| 2026-09-22 | O02 | 580d041 | **完成**。`SettingsRepository.Set` 同值短路：不写 `_store`、不触发 `Changed`。原先无条件写 `LocalSettings` 并扇出，Settings 页五个 Slider 拖动一次即上百次写盘 + 上百次扇出到外观重算/同步脏标记（`05` §C-07，页面侧 150 ms 去抖早已有，仓库侧这一半一直没做）。**与要点 1 有一处偏差**：比较对象取存储里的原始值且要求其合法，而非 `Read()` 的有效值——否则存了非法值时 `Set(key, 默认值)` 会被误判同值跳过，非法值永远留在存储里；现判据保住了「写默认值修复非法值」的旧行为。新增 9 条单测（`CountingSettingsStore` 记账：同值不写不广播、拖动形状只写真实变化、异值照常、写默认值被跳过、非法值/类型不符仍落盘修复、键缺失照常写、字符串按值比较、非法值仍抛）。已核对全部 `Changed` 订阅方（AppServices/LifecycleService/HostListViewModel/SettingsViewModel/SyncTriggers）无依赖同值广播的路径。`05` §3 的 C-07 复核说明已改为「已闭环」。verify 7 步全绿（Core 1438）。 |
| 2026-09-22 | O03 | 2e98cb8 | **完成**。两条泄漏路径收口：①视图侧 `SessionsPivot.Attach` 的匿名 lambda 订阅单例 `SessionsPaneViewModel.PropertyChanged`（解不掉）+ `ItemsSource` 绑单例集合（经 CollectionChanged 攥住 ListView），改具名处理器 + `Detach()`（含 ItemsSource 置空），`SessionsPane` 同样处理；②VM 侧 HostList/Main/Settings + 4 个 Sync VM在构造函数订阅仓库/SettingsRepository/SyncCoordinator 等应用级单例却无 `-=`，各补幂等 `Detach()`，页面 `OnNavigatedFrom` 调用，`MainViewModel.Detach` 级联 `Hosts.Detach` 但不碰单例 `Sessions`。订正 `VaultSetupViewModel` 里说反因果的注释（「VM 与页面同生命周期…无需退订」→ 正因协调器活得更久才必须退订）。**新增 `scripts/check-subscriptions.ps1`**（verify ④c）：受管事件具名订阅须同文件配对 `-=`，匿名委托订阅一律报错，同寿命 5 处逐条豁免写明理由（其中 `NativeForwarder` 那处经核对是自建自管的专用会话，随 `ActiveTunnel.Dispose` 释放，非泄漏）；已用临时探针文件双向验证（有违例 EXIT=1、删除后 EXIT=0）。原定「Core 侧回归」因 App VM 在 UWP 程序集、Core 测试工程（net8）引用不到而改为该门禁，验收项已如实改写。verify 8 步全绿（Core 1438、native 309）。⏳ 📱 首页↔终端页往返 50 次内存验证（已登记待办）。 |
| 2026-09-22 | O04 | 1f5bf64 | **完成**。逐页核查 19 个未接入页面，只有 3 页真有 C-02 形态（await 之后回头写本页 XAML 或导航）：`SettingsPage`（外观名、导出/清空日志 Toast）、`Sync/VaultSetupPage`、`Sync/SecurityRotatePage`，这 3 页接入 `NavigationLifetime`；其余按理由豁免而非加空仪式（`AccountSyncPage` 的 await 全是模态对话框，**用户已确认的动作必须执行完**，加守卫反而会静默丢弃撤销/删除；`MainPage` 唯一 await 是退出确认；其余 11 页代码隐藏零 await，异步在页面级 VM 内、O03 已收口）。**顺带修掉一个真缺陷**：`SecurityRotatePage.ShowRecoveryKeyAsync` 原写 `var ignored = RecoveryKeyDialog.ShowAsync(...)`，任务被丢弃、`GoBack()` 在对话框刚弹出时就执行，用户没机会保存轮换后的新恢复密钥（方法自己的注释从未成立；恢复密钥丢失不可找回）——改为与 `VaultSetupPage` 同一套「等勾选确认，未确认带警告重弹」。Core 补 `NavigationLifetime.Current`（事件处理器在 await 前取世代号，世代外返回 0 而首次 Begin 即到 1，故 `IsCurrent(0)` 恒 false）+ 4 条单测。verify 8 步全绿（Core 1442）。 |
| 2026-09-22 | O05 | 30e29ff | **完成**。47 处 `var ignore = …`（22 个文件）全部改为 `X().Forget("模块.动作", AppLog.Logger)`，非 Debug 代码计数归零。两处特殊：`DispatcherHelper.Post` 的 `RunAsync` 返回 `IAsyncAction`，改 `.AsTask().Forget(...)`——UI 封送原语自身的派发失败也要可观察，且日志走文件不经 dispatcher，不会递归；`StatusBarService` 的两处同理。`AsyncCommand` 迁移**未做**并写明理由（调用点几乎全是代码隐藏的 Click 处理器，改命令绑定要动 XAML，属 V04/V05 范围）。门禁增规则 C（`var ignore =` 即报错）。**修 O03 引入的门禁缺陷**：规则 C 初版的 `` 在生成脚本时被写成了字面退格符（0x08），正则恒不匹配——已用探针文件三条规则逐条验证（有违例 EXIT=1、清空后 EXIT=0）；同时把累加器换成 `List` 并修正 `.Add()` 内 `-f` 需再包一层括号的坑。verify 8 步全绿。 |
| 2026-09-22 | O06 | 41928b8 | **完成**。`UwpHttpTransport` 的 filter/client 改 `static readonly` 复用（`Windows.Web.Http` 连接池挂在 filter 上，每请求新建即每请求一次 TLS 握手；一个同步周期多请求 + 60 s 轮询+ 3 s 去抖上传，蜂窝网下延迟与耗电放大明显）。消息对象仍按请求创建释放；NoCache/NoCookies/AllowAutoRedirect=false 与超时、取消语义未变。不 Dispose：持有者 `AppServices` 与进程同寿命，提前释放会打断在飞行的同步请求。verify 8 步全绿。⏳ 📱 Wi-Fi/蜂窝各一次完整同步周期（已登记待办）。 |
| 2026-09-22 | O07 | d1bdb8b | **完成**。三项只做划算的两项：①去掉 `UploadAsync` 里 `PendingUpload.Document = doc.Clone()` 的整图深拷贝（`doc` 是现建独占快照，几行后 `BaseDocument = doc` 本来也没拷贝；store 活状态内共享无碍，`VaultCacheState.Clone()` 读时各自深拷贝）；②`SyncMerge` 三份输入改走新增的 `SyncDocumentWriter.WriteUnvalidated`，跳过 `Validate` 与 2 MiB 检查（输入要么刚被 Reader 校验过要么现建），合并结果仍由末尾 `SyncDocumentReader.Read` 全量校验（R8 兜底）。**放弃两项并写明理由**：`PendingUpload` 缓存规范 JSON 是坏交易（`SameContent` 比 `omitUpdatedAt` 变体，与上传已算出的含 `updatedAt` 版本不通用，缓存反而把「重放才付一次」变成「每次上传都付」）；「模型→JObject」直转会让 schema 出现第二个事实来源，漏字段即静默丢数据。**核查中发现更贵的一项**：`VaultCacheStore.State` 每次读都深拷贝三份完整文档、`SyncCoordinator` 里 33 处调用多数只读标量——立为 **O15**（`06-OPT-AUDIT §3 P1-15`），不并入本任务，任务总数 126→127。上传字节零偏差：`-Quick -Interop` 全绿；全量 verify 8 步全绿（Core 1442）。 |
| 2026-09-22 | O08 | dcee13d | **完成**。`Repository<T>` 维护 id→下标字典，`_items` 的 5 处整体替换收口到新的 `SetItems()`；重复 id 沿用「第一个命中」（建字典时只记首次出现，不用 `Add` 以免在损坏数据上抛）。`KnownHostRepository` 新增 `FindAsync(host, port)`：懒建索引 + `Changed` 失效，键形如 `"22/example.com"`（端口在前，`/` 不可能出现在端口数字里，无拼接歧义），`SessionManager.FindKnownAsync` 改为转发——原先跳板链每跳线性扫一次、落盘再扫一次。新增 7 条回归（索引跨 Add/AddMany/Update/Remove/ReplaceAll 正确、冷加载后可查、null/空 id、重复 id 取第一个；FindAsync 大小写不敏感/端口精确、写入后失效重建、拼键无歧义）。verify 8 步全绿（Core 1449）。 |
| 2026-09-22 | O09 | 8ddc26c | **完成，但改的不是审查设想的那件事**。核对 `vterm.h:253-279` 后确认 libvterm **不暴露** DECCKM(1)/1006/2004 的读取接口（`VTERM_PROP_MOUSE` 只到 1000/1002/1003），手写扫描器省不掉——审查条目 1 的前提不成立。改为修掉扫描器的真缺陷：旧实现在扫描**前**把缓冲截成「最后 256 字节」，一次 32 KB 读块里靠前的 DECSET 被静默丢弃（vim 先发模式切换再刷满屏正好踩中，表现为方向键/粘贴时灵时不灵）。现扫全量、只留未完成尾巴；carry 为空时直接扫入参不拷贝；尾巴超 256 字节判畸形丢弃防无界增长；扫描抽成 `scanDecModes(buf,size)`。条目 2（softwrap 增量化）**不做**并写明理由：48×30 网格上约 60 次操作，引入 damage 区间跟踪得不偿失。新增 6 条测试，其中「大块前部 DECSET 不丢」为先写后修的回归（修前实测 FAILED）。vterm_screen_test 42 条全过、ctest 315 全过、verify 8 步全绿。 |
| 2026-09-22 | O10 | cf420e4 | **完成**。`BidirectionalPump` 的两个方向缓冲从裸 `std::string` 换成带读偏移的 `StreamBuffer`（`consume` 只推进 `head_`，送完归零，已消费过半且超 16 KiB 才压实一次），去掉每次部分写的 `erase(0, sent)` memmove——缓冲逼近 256 KiB 上限时原本接近 O(n²)，SFTP-over-tunnel 与 SOCKS5 大流量持续踩；与同仓 `channel.cpp` 的 `PendingWriteQueue` 写法对齐。`EventLoop::run()` 的 `pollDescriptors` 提升为成员复用（原为循环体内局部 vector，每次唤醒堆分配一次，N 条会话线程各一份）。**派发时的二次 `find()` + `std::function` 拷贝保留并写明理由**：回调可能在执行中注销自己或别的 socket、销毁 map 节点，存指针会在派发中途悬垂。verify 8 步全绿（ctest 315）。 |
