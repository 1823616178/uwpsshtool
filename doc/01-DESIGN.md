# Lumia SSH 总体设计

> 项目：`G:\code\uwpsshtool`（应用显示名暂定 **Lumia SSH**）
> 目标设备：Lumia 950（Windows 10 Mobile），兼顾 Continuum 大屏 + 键鼠
> 参考实现（只读）：
>
> | 参考 | 路径 | 拿什么 |
> |---|---|---|
> | 鸿蒙端 | `C:\Users\lx182\DevEcoStudioProjects\ssh_client_ohos` | 功能形态、原生核心（`entry/src/main/cpp/`：ssh/term/crypto/io 与 GoogleTest 用例）、终端交互设计、会话管理/后台保活策略、错误码体系 |
> | 桌面端 | `E:\code\ssh-tool` | **同步协议权威实现**（`src/shared/sync-*.ts`、`src/main/sync/*`、`src/main/security/crypto-vault.ts`）、SOCKS5（`src/main/tunnel/socks5.ts`）、隧道模型、测试场景（`test/*.test.ts`） |
> | 服务端 | `G:\code\ssh-tool-server` | `/api/v1`，按原样复用；接口文档副本见鸿蒙端 `docs/api-v1.md` |
>
> 文档日期：2026-09-17

---

## 0. 一句话定位

Windows 10 Mobile 原生 UWP SSH 终端：**C# / XAML 界面 + C++/CX 原生核心（libssh2 + OpenSSL + libvterm + libargon2）**，
终端用 Win2D 绘制；配置通过端到端加密云同步与桌面端 ssh-tool、鸿蒙端在同一账号下双向同步。

---

## 1. 目标平台事实与约束

### 1.1 设备与系统

| 项 | 事实 | 对设计的影响 |
|---|---|---|
| SoC / 架构 | Snapdragon 808（6 核），W10M 用户态为 **ARM32** | 只能发 `ARM` 包；原生库全部按 ARM32 UWP 编译 |
| 内存 | 3 GB，W10M 对前台应用有内存上限（高内存设备约数百 MB 级，`MemoryManager.AppMemoryUsageLimit` 读取） | 预算：常驻 < 150 MB，4 会话 + 5 万行回滚 < 300 MB；回滚缓冲在 native 按需取窗口 |
| 屏幕 | 5.2" 1440×2560，缩放 400%，**有效像素 360×640 epx** | 竖屏终端约 48 列（12epx 字号），默认字号需小；键条必须紧凑 |
| 系统版本 | 最终版 Windows 10 Mobile 1709（build 10.0.15254.x），API 合约等同 **10.0.15063**（Creators Update） | `TargetPlatformMinVersion=10.0.15063.0`；不可用 Acrylic、NavigationView、SwipeControl、ColorPicker、InfoBar、TeachingTip（均 ≥16299） |
| .NET | UWP + .NET Native（Release 必须 .NET Native 编译） | 反射受限：JSON 不用反射序列化（手写读写器）；共享库最高 `netstandard1.4`（netstandard2.0 需 min 16299） |
| 语言 | C# 7.3 | 无 nullable reference、无 switch 表达式、无 `using` 声明 |
| 硬件返回键 | `SystemNavigationManager.BackRequested` | 所有页面/弹层都要定义返回行为（见 UI §4） |
| 状态栏 | `Windows.UI.ViewManagement.StatusBar`（Mobile 扩展 SDK） | 终端页沉浸模式隐藏状态栏；`ApiInformation.IsTypePresent` 守卫 |
| 软键盘 | `InputPane`，Word Flow 键盘；中文拼音 IME 有组合态 | 终端输入走隐藏 TextBox 方案 + 组合态判定（§7.5），需 SP05 验证 |
| Continuum | 通过 Display Dock / Miracast 输出到大屏，第二窗口为桌面式窗口，支持蓝牙/USB 键鼠 | 等价于鸿蒙端的 2in1：宽屏布局、多标签分屏、物理键盘、鼠标选择与滚轮（§7.6） |
| 网络能力声明 | `internetClient`、`internetClientServer`、`privateNetworkClientServer` | **连局域网 SSH 服务器必须声明 `privateNetworkClientServer`**，否则 10.x/192.168.x 连接会被静默拦截 |
| 回环隔离 | UWP 默认禁止其他应用连本应用监听的 127.0.0.1 端口 | 本地端口转发（-L）在手机上主要服务局域网客户端与应用内功能，UI 需如实提示（§11.2） |
| 后台 | 切走/锁屏后应用被挂起；`ExtendedExecutionSession` 可延长但可被系统随时撤销（省电模式必撤） | 保活是「尽力而为 + 优雅断开 + 回前台原地重连 + tmux 附着」降级链（§10） |

### 1.2 开发与部署环境（X01 落实并回填实测版本）

| 项 | 选择 | 说明 |
|---|---|---|
| IDE | **VS2026 18.10 Professional**（构建 C#，2026-09-17 X01 实测回填；原方案 VS2019 16.11 本机未安装） | 含「通用 Windows 平台开发」工作负载；能否直接部署/调试 W10M 待真机实测，默认走 WinAppDeployCmd。并存 VS2017 15.9 提供 v141 工具集（Native 的 ARM32 编译器来源，因 VS2026 的 v142/v145 均无 ARM32） |
| Windows SDK | **10.0.19041**（Target，X01 回填：本机无 17763）+ Min 固定 10.0.15063 | Windows 11 SDK 22621 起不再支持 ARM32，禁止升级到 ≥22621；本机 26100 仅作存在记录，不使用 |
| UWP 包 | `Microsoft.NETCore.UniversalWindowsPlatform` **6.2.14**（SP01 已结：真机 .NET Native 运行通过，**无需退 5.4.x**） | 版本号写入 `Directory.Build.props` 统一管理 |
| 部署 | 手机开「开发人员模式」→ USB 或 Wi-Fi；`WinAppDeployCmd.exe install -file x.appx -ip <phone> -pin <pin>` | 依赖包（VCLibs、.NET Native Runtime/Framework ARM）一并安装 |
| 测试 | `dotnet test`（Core 纯逻辑，宿主机 net8）；CMake + GoogleTest（native 纯 C++，宿主机 x64 MSVC）；真机手工验收清单 | 让绝大部分逻辑不依赖真机就能自证 |
| 同步测试服务器 | `http://123.161.179.32:46926`（客户端自动拼 `/api/v1`） | 与鸿蒙端一致；明文 HTTP 风险见 §12.3 |

---

## 2. 需求清单与可验收目标

| 编号 | 需求 | 可验收目标 |
|---|---|---|
| R1 | 在 Lumia 950 上可用的 SSH 终端 | 密码/公钥/keyboard-interactive 认证；xterm-256color + 真彩；能跑 vim/htop/tmux；中文与宽字符对齐；`cat` 1 MB 文本不卡死（≥30 fps） |
| R2 | 移动端输入好用 | 功能键条（Esc/Tab/Ctrl/Alt/方向/Home/End/PgUp/PgDn 与常用符号），Ctrl/Alt 粘滞与锁定；中文输入法组合期不误发；长按选择复制、粘贴含多行确认；单指滚动回滚、双指缩放字号 |
| R3 | 主机管理 | 主机增删改、分组（折叠持久化）、搜索、快速连接、测试连接、TOFU 指纹确认与变更拦截、最近使用排序 |
| R4 | 配置同步（与桌面端/鸿蒙端互通） | 同账号下：桌面改主机 → 手机看到；手机改 → 桌面看到；断网修改联网补传；冲突可解；本机专有字段不被冲掉；密码/私钥同步为独立开关默认关；关闭敏感同步时轮换密钥 |
| R5 | 功能完善 | 多会话、会话切换、断线重连（倒计时）、tmux 自动附着与初始命令、命令片段、私钥管理（导入/生成/导出公钥）、known_hosts 管理、外观主题（字体/字号/配色/光标）、SFTP 浏览与传输、端口转发（L/R/D，relay 只编辑同步不运行）、ProxyJump |
| R6 | Continuum 大屏 | 宽屏主从布局、多标签 + 分屏、物理键盘全键位与快捷键、鼠标拖选/双击选词/滚轮/右键菜单、终端鼠标上报（SGR 1006） |
| R7 | 续航与后台 | 终端页常亮三档；切走/锁屏时尽力保活；被系统收回时优雅断开并在回前台时原地重连；网络切换主动探测 |
| R8 | 安全 | 凭据仅以 DPAPI（`DataProtectionProvider("LOCAL=user")`）加密落盘；日志脱敏；同步文档端到端加密；主机指纹变更拒绝连接 |

**明确不做（首版）**：Mosh/Telnet/串口、SSH 服务端、内置编辑器、Zmodem、团队共享、Windows 10 on ARM 刷机形态的专门适配。

---

## 3. 技术选型与关键决策

### 3.1 总体选型

| # | 决策 | 结论 | 理由 | 兜底（由对应 Spike 触发） |
|---|---|---|---|---|
| D1 | 应用框架 | **UWP（C# + XAML）**，min 15063 | W10M 唯一原生应用模型；XAML 适配 Continuum 自适应 | — |
| D2 | SSH 内核 | **libssh2 1.11.x + OpenSSL 3.x（C，编进 C++/CX 组件）** | 与鸿蒙端同一内核，可直接移植其 `cpp/ssh`、`cpp/term`、`cpp/crypto` 与 200+ GoogleTest 用例；算法齐（ed25519、curve25519、rsa-sha2-256/512、aes-gcm/ctr）；BSD 许可 | SP02/SP03 失败 → 方案 B：SSH.NET 2020.0.x（netstandard1.3/uap10）+ 托管 VT 解析；代价：无 rsa-sha2 用户认证（OpenSSH ≥8.8 默认禁 ssh-rsa，RSA 密钥登录会失败）、性能与算法覆盖较差 |
| D3 | 原生组件形态 | **C++/CX Windows Runtime Component**（`SshTool.Native`） | 在 W10M ARM 上久经验证；与 C# 互操作零配置；C++/WinRT 需较新工具链，W10M 真机经验少 | 不需要 |
| D4 | 终端仿真 | **libvterm 0.3.x（native）** + 鸿蒙端 16 字节单元格网格/脏行位图/revision/回滚环形缓冲 | 移植即用，已有 VT 语料测试 | — |
| D5 | 终端渲染 | **Win2D（`Win2D.uwp`，版本由 SP04 锁定支持 15063 的最高版）CanvasControl + 行缓存离屏目标 + 单一全局帧调度（`CompositionTarget.Rendering`）** | GPU 绘制文本；只重绘脏行；多窗格共用一条帧循环 | SP04 < 30 fps → 降级：按行 `TextBlock` 虚拟化 + 只更新脏行（放弃逐格背景色合批），或降帧到 30 fps 批量刷新 |
| D6 | native → C# 数据通路 | **每帧 `CopyDirtyRows(WriteOnlyArray<uint8>)` 拷贝脏行**，事件只投递轻量通知 | WinRT ABI 做零拷贝需 `IBufferByteAccess` + unsafe，收益小；50×30×16B ≈ 24 KB/帧，拷贝成本可忽略 | — |
| D7 | 共享逻辑库 | **`SshTool.Core`（netstandard1.4，C# 7.3）**：模型、校验、同步（序列化/合并/协调器/API 客户端）、键位映射、选择模型、窗格树、自动执行命令 | 能在宿主机 `dotnet test` 快速自证，AI 分次编码的主要质量保障 | — |
| D8 | 本地数据存储 | **JSON 文件仓库**（LocalFolder/`data/*.json`，原子写：写临时文件 → `MoveAndReplaceAsync`），内存缓存 + 变更事件 | 数据量小（主机数百级）；避免 W10M 上 SQLite 原生依赖与 .NET Native 兼容问题；便于同步整份映射 | 若主机 > 2000 出现性能问题再换 SQLite（不预期） |
| D9 | JSON 库 | **Newtonsoft.Json 12.0.3（netstandard1.0/1.3 目标）仅用 `JsonTextReader/JsonTextWriter/JObject`**，禁止反射式 `SerializeObject<T>`（SP01 已结：2026-09-18 Lumia 950 上 ARM Release/.NET Native 包 9/9 全 PASS，键序、Unicode+emoji、ulong 上限、时间格式均无偏差） | .NET Native 下反射序列化需要 rd.xml 且易静默丢字段；同步文档需要严格键序与严格校验 | 若包在 W10M 有问题 → `Windows.Data.Json`（仅 App 层可用） |
| D10 | 凭据存储 | **`DataProtectionProvider("LOCAL=user")` 加密的 `secure/secrets.bin`**（键值表） | `PasswordVault` 会随微软账号漫游且条目数有限；DPAPI-NG 绑定本机用户 | SP06 验证不可用 → `PasswordVault`（关闭漫游不可控，需文案提示） |
| D11 | HTTP | **`Windows.Web.Http.HttpClient` + `HttpBaseProtocolFilter`（关缓存、关 Cookie、关自动重定向）** | 支持 HEAD、自定义 If-Match；**默认会缓存 GET，必须关**（§12 坑表） | — |
| D12 | 同步协议 | **与桌面端 SyncDocumentV1 完全互通**；同步算法按桌面端 `sync-coordinator.ts` 移植（比鸿蒙端实现更完整：pendingUpload 重放、pendingVaultSetup、首次导入确认、远端删除确认、keyVersion 检查、revision 回退保护） | 同一账号三端共用；桌面端有完整测试 | — |
| D13 | 保险库密码学位置 | **native（OpenSSL + libargon2），移植鸿蒙端 `crypto/vault.cpp`，但 KDF 参数改为读信封** | Argon2id 64 MiB/3 轮在 Lumia 上约数秒，必须原生且离开 UI 线程；鸿蒙端黄金向量可复用 | — |
| D14 | 帧调度 | **全应用唯一 `CompositionTarget.Rendering` 订阅**，遍历可见窗格按 revision 决定重绘，无脏数据时退订以省电 | 与鸿蒙端 D14 同理 | — |
| D15 | MVVM | 自写轻量基础设施（`ObservableObject`、`RelayCommand`、`AsyncCommand`、`NavigationService`、`DialogService`、简单服务定位器） | Windows Community Toolkit 新版本需 min 16299；自写代码量小可控 | — |
| D16 | 依赖构建 | OpenSSL：vcpkg overlay triplets `native/triplets/*-uwp-v141`（v141 工具集；失败则 `perl Configure VC-WIN32-ARM-UWP` + nmake）；libssh2 / libvterm / argon2：**源码直接编进 native 组件项目**（vendored 到 `native/third_party`） | 减少三方构建系统适配；源码编译便于打 UWP 小补丁 | SP02 已验证：3.6.3 四 triplet 全过（ARM 需 /LIBPATH 短路径补丁，见 `native/NATIVE-BUILD.md` §4） |
| D17 | 原生单测 | 纯 C++ 核心（`native/core`）同时编成宿主机 x64 静态库跑 GoogleTest；bridge 层不进单测 | 复用鸿蒙端 `cpp/tests` | — |
| D18 | 私钥同步 | 首版先同步密码/短语；私钥同步（base64 + format + SHA256 公钥指纹，与桌面端 ssh2 结果一致）作为 S10 独立任务 | 指纹必须与桌面端逐字节一致，单独验证 | — |

### 3.2 依赖清单

| 库 | 版本 | 许可 | 接入方式 |
|---|---|---|---|
| OpenSSL | **3.6.3**（SP02 实测编过 ARM/x86/x64-UWP 与宿主机；vcpkg `2026.07.29` port） | Apache-2.0 | 预编译静态库 `native/prebuilt/<arch>/`，不入库，`scripts/build-openssl.ps1` 生成 |
| libssh2 | 1.11.1 | BSD-3 | vendored 源码 |
| libvterm | 0.3.3 | MIT | vendored 源码 |
| phc-winner-argon2 | 20190702 | CC0 / Apache-2.0 | vendored 源码 |
| zlib | 1.3.x | zlib | vendored 源码（libssh2 压缩可选，默认关） |
| Win2D.uwp | SP04 锁定 | MIT | NuGet |
| Newtonsoft.Json | 12.0.3 | MIT | NuGet（Core 与 App） |
| JetBrains Mono | 2.304 | OFL-1.1 | 随包字体 `Assets/Fonts` |
| GoogleTest | 1.14 | BSD-3 | CMake FetchContent（仅宿主机测试） |
| xUnit | 2.x | Apache-2.0 | 仅 `SshTool.Core.Tests` |

---

## 4. 架构

### 4.1 分层

```
┌──────────────────────────────────────────────────────────────────────┐
│ SshTool.App（UWP，C# 7.3 + XAML）                                      │
│  Views/        Pages、UserControls（TerminalView、KeyBar、HostRow…）   │
│  ViewModels/   页面状态（INotifyPropertyChanged）                       │
│  Platform/     平台适配：UwpFileSystem、DpapiSecretStore、UwpHttpTransport│
│                LifecycleService、KeepAwakeService、NetworkMonitor、     │
│                Haptics、ClipboardService、DialogService、Navigation     │
│  Terminal/     FrameScheduler、TerminalRenderer(Win2D)、InputHandler    │
└───────────────┬───────────────────────────────┬──────────────────────┘
                │ 引用（纯 C#）                   │ WinRT 投影（C++/CX）
┌───────────────┴──────────────────┐  ┌─────────┴───────────────────────┐
│ SshTool.Core（netstandard1.4）    │  │ SshTool.Native（C++/CX WinRT 组件）│
│  Models/  Validation/             │  │  Bridge/  SshSession、TerminalScreen│
│  Storage/ 仓库 + 迁移（接口化 IO） │  │           VaultCrypto、KeyTool      │
│  Sessions/ SessionManager 逻辑     │  │           SftpSession、Forwarder    │
│  Sync/    Api、Auth、Vault 状态、  │  │  → 调用 native/core（纯 C++17）      │
│           Serializer、Merge、      │  └─────────┬───────────────────────┘
│           Coordinator、LocalAdapter│            │ 静态链接
│  Terminal/ KeyMap、Selection、     │  ┌─────────┴───────────────────────┐
│           PaneTree、StickyModifiers│  │ native/core                       │
│  Common/  Result、错误码、Logger 接口│  │  io/ ssh/ term/ crypto/ sftp/ fwd/ │
└──────────────────────────────────┘  │  third_party: libssh2 libvterm argon2│
                                       │  prebuilt: OpenSSL libcrypto        │
                                       └─────────────────────────────────────┘
```

**依赖方向**：App → Core、App → Native；Core 不引用 Native（通过接口 `ISshSessionFactory`、`IVaultCrypto` 等由 App 注入实现），保证 Core 可在宿主机单测。

### 4.2 线程模型

| 线程 | 职责 | 禁止 |
|---|---|---|
| UI 线程（CoreDispatcher） | XAML、Win2D 绘制、输入事件 | 加解密、JSON 大文档解析、阻塞 IO |
| 帧回调（`CompositionTarget.Rendering`，UI 线程） | 遍历可见窗格，按 revision 拷贝脏行并重绘 | 等待网络 |
| native 会话 I/O 线程（每会话 1 条 `std::thread`） | Winsock 非阻塞 + `WSAPoll`、libssh2 状态机、libvterm 喂数据、更新网格 | 回调里直接碰 XAML |
| native 任务线程（`concurrency::create_async`） | Argon2id、AES-GCM、密钥生成、SFTP 批量操作 | — |
| .NET 线程池（`Task.Run`） | 同步协调器、JSON 序列化、文件读写 | 直接改 ViewModel 绑定属性（需回 Dispatcher） |

native → C# 事件经 WinRT event 在 I/O 线程触发，C# 侧统一由 `DispatcherQueue`（15063 无 `DispatcherQueue`，用 `CoreDispatcher.RunAsync`）封送。
> 实现纪律（2026-09-18 踩坑）：封送一律走 `Infrastructure.DispatcherHelper`。**`Window.Current` 是线程静态的，后台线程上恒为 `null`**，
> 后台回调里取 `Window.Current.Dispatcher` 必然 NRE；`DispatcherHelper` 在 `OnLaunched` 缓存 UI 线程的 `CoreDispatcher`，
> 兜底用可跨线程访问的 `CoreApplication.MainView.CoreWindow.Dispatcher`。同理 `UISettings.ColorValuesChanged` 在后台线程触发。
**终端字节流不走事件**：I/O 线程只把 `ContentDirty(sessionId)` 合并投递（同一帧内多次只投一次）。

### 4.3 关键数据通路

**终端输出**
```
socket → libssh2_channel_read → vterm_input_write → Grid（16B/cell）+ dirtyBitmap + revision++
          └→ 若本帧未通知：ContentDirty 事件 → FrameScheduler.Wake()
UI 帧：FrameScheduler 遍历可见 TerminalView：
        screen.Revision != lastRevision → screen.CopyDirtyRows(rowsBuf, bitmapBuf)
        → TerminalRenderer 重绘脏行到行缓存 CanvasRenderTarget → CanvasControl.Invalidate()
无窗格有脏数据连续 N 帧 → 退订 Rendering（省电），下次 Wake 再订阅
```

**终端输入**
```
软键盘：隐藏 TextBox → InputHandler（组合态过滤、退格/回车识别）→ StickyModifiers → KeyMap → bytes
物理键：CoreWindow.KeyDown / CharacterReceived / AcceleratorKeyActivated → ShortcutMap（先匹配应用快捷键）→ KeyMap
键条：KeyBar 按钮 → StickyModifiers → KeyMap
→ 聚焦窗格的 SshSession.Write(bytes)（I/O 线程发送）
```

**同步**
```
仓库保存 → ConfigChanged → SyncCoordinator.MarkDirty（防抖 3 s）→ PerformSync（线程池）
  → LocalAdapter.Build → Api（HEAD/GET/PUT）→ VaultCrypto（native）→ Merge → LocalAdapter.Apply → 仓库写回
  → SyncState 变化 → Dispatcher → AccountSyncViewModel / 主页同步状态图标
```

---

## 5. 工程目录（X02 建立，之后保持一致）

```
uwpsshtool/
├── CLAUDE.md                         AI 上下文（指向 doc/）
├── Directory.Build.props             统一版本号、LangVersion=7.3、NuGet 版本
├── SshTool.sln
├── doc/                              本规划文档
├── scripts/
│   ├── verify.ps1                    一键门禁：Core 测试 + native 测试 + App 构建（x64 Debug，可选 ARM Release）
│   ├── build-openssl.ps1             OpenSSL 多架构 UWP 静态库
│   ├── fetch-third-party.ps1         拉取并校验 libssh2/libvterm/argon2/zlib 源码（固定版本 + SHA256）
│   ├── package-arm.ps1               Release ARM 打包
│   ├── deploy-phone.ps1              WinAppDeployCmd 安装到手机
│   └── check-error-codes.ps1         C# 与 C++ 错误码表对拍
├── tools/
│   └── sync-vectors/                 Node 脚本：用桌面端算法生成跨端测试向量（只读引用 E:\code\ssh-tool 的依赖版本）
├── src/
│   ├── SshTool.Core/                 netstandard1.4
│   │   ├── Common/                   Result、Guard、Clock、IdGenerator、ILogger、SshErrorCode
│   │   ├── Models/                   Host、HostGroup、Tunnel、Snippet、AppearanceProfile、KnownHost、KeyEntry、Settings 值类型
│   │   ├── Validation/               HostValidator、TunnelValidator、GroupValidator（对齐同步 schema 限制）
│   │   ├── Storage/                  IFileSystem、JsonStore<T>、各 Repository、Migrations、ISecretStore、SecretKeys
│   │   ├── Sessions/                 SessionInfo、SessionManager、ISshSessionFactory、ReconnectPolicy、AutoRun、CredentialResolver
│   │   ├── Terminal/                 KeyMap、ShortcutMap、StickyModifiers、SelectionModel、PaneTree、KeyBarLayout、SnippetTemplate
│   │   ├── Sync/
│   │   │   ├── Protocol/             SyncConstants、SyncDocumentV1 模型、SyncDocumentReader/Writer（严格校验）
│   │   │   ├── Api/                  IHttpTransport、ApiClient、ApiError、Dto
│   │   │   ├── Auth/                 AuthStore、AuthService、DeviceDescriptor
│   │   │   ├── Vault/                IVaultCrypto、VaultCacheState、VaultCacheStore
│   │   │   ├── SyncMerge.cs
│   │   │   ├── SyncLocalAdapter.cs
│   │   │   └── SyncCoordinator.cs
│   │   └── Forwarding/               Socks5Parser（移植 socks5.ts）、TunnelRuntimeState
│   ├── SshTool.Native/               C++/CX Windows Runtime Component（ARM/x86/x64）
│   │   ├── Bridge/                   SshSession、TerminalScreen、VaultCrypto、KeyTool、SftpSession、Forwarder、Marshal 工具
│   │   └── pch.h / SshTool.Native.vcxproj（引用 native/core 源文件）
│   └── SshTool.App/                  UWP 应用
│       ├── App.xaml(.cs)             启动、生命周期、服务组装
│       ├── Themes/                   Tokens.xaml、Tokens.Dark.xaml、Tokens.Light.xaml、Controls.xaml
│       ├── Assets/                   图标、磁贴、Fonts/JetBrainsMono-*.ttf
│       ├── Strings/zh-CN/Resources.resw、Strings/en-US/Resources.resw
│       ├── Infrastructure/           ObservableObject、RelayCommand、AsyncCommand、NavigationService、DialogService、ServiceRegistry、DispatcherHelper
│       ├── Platform/                 UwpFileSystem、DpapiSecretStore、UwpHttpTransport、LifecycleService、KeepAwakeService、NetworkMonitor、Haptics、ClipboardService、NativeSshSessionFactory、NativeVaultCrypto、FileLogger
│       ├── Terminal/                 FrameScheduler、TerminalRenderer、FontMetrics、SoftKeyboardInput、HardwareKeyboardInput、PointerInput、TerminalView.xaml
│       ├── Controls/                 KeyBar、StatusDot、Banner、EmptyState、SectionHeader、ColorSwatchPicker、LoadingOverlay、RandomArtView
│       ├── Views/                    各 Page（见 UI 文档 §4）
│       ├── Dialogs/                  HostKeyDialog、CredentialDialog、KbdInteractiveDialog、PasteConfirmDialog、RecoveryKeyDialog…
│       ├── ViewModels/
│       └── Package.appxmanifest
├── native/
│   ├── core/                         纯 C++17（可宿主机编译）
│   │   ├── io/                       EventLoop（WSAPoll + 唤醒 socket 对）、SessionThread、Timer
│   │   ├── ssh/                      session、hostkey、auth、channel、keepalive、reconnect_policy、error_codes、agent
│   │   ├── term/                     vterm_screen、grid、scrollback、frame_sync
│   │   ├── crypto/                   sync_params.h、aad、vault、keytool
│   │   ├── sftp/                     sftp_session、transfer
│   │   └── fwd/                      direct_tcpip、remote_listen、jump_transport
│   ├── third_party/                  libssh2/ libvterm/ argon2/ zlib/（fetch 脚本生成，不入库或以子模块/压缩包固定）
│   ├── prebuilt/                     <arch>/include lib（OpenSSL，不入库）
│   └── tests/                        CMakeLists.txt + *_test.cpp（移植鸿蒙端 cpp/tests）+ sshd_testkit
└── tests/
    └── SshTool.Core.Tests/           net8.0 xUnit，引用 SshTool.Core
```

---

## 6. 原生层设计

### 6.1 模块与移植来源

| 模块 | 来源（鸿蒙端 `entry/src/main/cpp/`） | UWP 改动点 |
|---|---|---|
| `io/EventLoop` | `io/EventLoop.*`（epoll） | 换 `WSAPoll`；唤醒用本进程内回环 UDP socket 对（SP03 验证 UWP 允许同进程回环，否则退化为 50 ms 轮询超时）；`WSAStartup` 进程级一次 |
| `io/SessionThread` | `io/SessionThread.*` | `pthread` → `std::thread`；线程名用 `SetThreadDescription`（有则用） |
| `ssh/session` | `ssh/session.*` | 状态机不变；DNS 用 `getaddrinfo`；非阻塞 connect 用 `WSAEWOULDBLOCK` 判定 |
| `ssh/hostkey` | `ssh/hostkey.*` | 不变（SHA256 指纹 + randomart） |
| `ssh/auth` | `ssh/auth.*` | 不变（password / publickey_frommemory / keyboard-interactive 回调）；清零用 `SecureZeroMemory`/`OPENSSL_cleanse` |
| `ssh/channel` | `ssh/channel.*` | 不变（pty、resize、env、exec、exit-status） |
| `ssh/keepalive` `reconnect_policy` `error_codes` | 同名 | 不变 |
| `ssh/agent` | `ssh/agent.*` | 不变（应用内密钥缓存，超时清除） |
| `term/*` | `term/vterm_screen`、`grid`、`scrollback`、`frame_sync` | 不变 |
| `crypto/*` | `crypto/sync_params.h`、`aad.*`、`vault.*` | **Argon2 参数改为入参**（来自信封 `kdfParameters`，并按桌面端范围校验 memory 8192–1048576、iterations 1–20、parallelism 1–16）；解密前先核对 `ciphertextHash`；Base64 解码要求规范形式 |
| `crypto/keytool` | 新增 | OpenSSL 生成 ed25519 / RSA-3072；解析 OpenSSH（openssh-key-v1）与 PEM；导出 `ssh-ed25519 AAAA… comment`；SHA256 公钥指纹 |
| `sftp/*` `fwd/*` | 新增（鸿蒙端未实现） | libssh2 SFTP、direct-tcpip、forward_listen、跳板传输回调 |

### 6.2 WinRT 接口草图（`SshTool.Native` 命名空间，C++/CX）

> 以下为契约草图，N09/T03/S03/K01/F01/F03 实现时可微调，但**改动必须回写本节**。

```cpp
public enum class SessionState { Idle, Connecting, Handshaking, Authenticating, Established, Disconnected, Error };

public ref class ConnectOptions sealed {           // 由 C# 填
  property String^ Host; property int Port; property String^ Username;
  property int ConnectTimeoutMs;   // 默认 15000
  property int KeepaliveSeconds;   // 0 关闭
  property String^ TermType;       // xterm-256color
  property int Cols; property int Rows;
  property IMap<String^, String^>^ Env;
  property String^ JumpSessionId;  // ProxyJump：上级会话（F05）
};

public ref class HostKeyInfo sealed { property String^ KeyType; property String^ FingerprintSha256; property String^ RandomArt; };
public ref class HostKeyCheckEventArgs sealed { property HostKeyInfo^ Info; void Accept(); void Reject(); Deferral^ GetDeferral(); };
public ref class AuthPromptEventArgs sealed {       // keyboard-interactive
  property String^ Name; property String^ Instruction;
  property IVectorView<String^>^ Prompts; property IVectorView<bool>^ Echo;
  void Respond(IVectorView<String^>^ answers); void Cancel(); Deferral^ GetDeferral();
};
public ref class StateChangedEventArgs sealed { property SessionState State; property int ErrorCode; property String^ Detail; };

public ref class SshSession sealed {
public:
  SshSession();
  property String^ Id { String^ get(); }
  property SessionState State { SessionState get(); }
  property TerminalScreen^ Screen { TerminalScreen^ get(); }
  IAsyncOperation<int>^ ConnectAsync(ConnectOptions^ options);          // 握手 + HostKeyCheck 事件
  IAsyncOperation<int>^ AuthenticatePasswordAsync(String^ password);
  IAsyncOperation<int>^ AuthenticatePublicKeyAsync(const Array<uint8>^ privateKeyPem, String^ passphrase);
  IAsyncOperation<int>^ AuthenticateKeyboardInteractiveAsync();         // 触发 AuthPrompt 事件
  IAsyncOperation<int>^ OpenShellAsync(int cols, int rows);
  void Write(const Array<uint8>^ data);
  void Resize(int cols, int rows);                                       // 本地网格 + request_pty_size
  void ProbeNow();                                                       // 5 s 判定窗口
  void Close();
  event EventHandler<StateChangedEventArgs^>^ StateChanged;
  event EventHandler<HostKeyCheckEventArgs^>^ HostKeyCheck;
  event EventHandler<AuthPromptEventArgs^>^ AuthPrompt;
  event EventHandler<Object^>^ ContentDirty;     // 合并投递
  event EventHandler<String^>^ TitleChanged;
  event EventHandler<Object^>^ Bell;
  event EventHandler<int>^ ChannelClosed;        // exit-status
};

public ref class TerminalScreen sealed {
public:
  property int64 Revision; property int Cols; property int Rows;
  property int CursorRow; property int CursorCol; property bool CursorVisible;
  property bool AltScreen; property bool AppCursorKeys; property bool BracketedPaste;
  property int MouseMode;          // 0 关 / 1000 / 1002 / 1003
  property bool MouseSgr;          // 1006
  property int ScrollbackCount;
  // rowsOut 大小 = Rows*Cols*16；dirtyOut 大小 = ceil(Rows/8)；返回是否有变化
  bool CopyDirtyRows(WriteOnlyArray<uint8>^ rowsOut, WriteOnlyArray<uint8>^ dirtyOut);
  // 从底部向上偏移 offset 行的视口整窗拷贝（回滚浏览）
  void CopyViewport(int offset, WriteOnlyArray<uint8>^ rowsOut);
  String^ GetText(int startRow, int startCol, int endRow, int endCol, int offset); // 选择复制
};

public ref class VaultEnvelope sealed { /* keyVersion、passwordWrappedKey、passwordWrapNonce、recoveryWrappedKey、recoveryWrapNonce、kdfSalt、kdfMemory、kdfIterations、kdfParallelism */ };
public ref class VaultSetupResult sealed { property VaultEnvelope^ Envelope; property String^ RecoveryKey; property String^ VaultKeyBase64; };
public ref class DocumentEnvelope sealed { /* schemaVersion keyVersion algorithm nonce ciphertext ciphertextHash */ };
public ref class VaultCrypto sealed {
  static IAsyncOperation<VaultSetupResult^>^ CreateAsync(String^ syncPassword, int keyVersion);
  static IAsyncOperation<String^>^ UnwrapWithPasswordAsync(VaultEnvelope^ env, String^ syncPassword); // 失败返回 nullptr
  static IAsyncOperation<String^>^ UnwrapWithRecoveryKeyAsync(VaultEnvelope^ env, String^ recoveryKey);
  static IAsyncOperation<DocumentEnvelope^>^ EncryptDocumentAsync(String^ vaultKeyB64, String^ vaultId, int schemaVersion, int keyVersion, const Array<uint8>^ utf8Plaintext);
  static IAsyncOperation<IBuffer^>^ DecryptDocumentAsync(String^ vaultKeyB64, String^ vaultId, DocumentEnvelope^ env); // 失败返回 nullptr
};

public ref class KeyInfo sealed { property String^ KeyType; property int Bits; property String^ Format; property bool Encrypted; property String^ PublicKeyOpenSsh; property String^ FingerprintSha256; };
public ref class KeyTool sealed {
  static IAsyncOperation<IBuffer^>^ GenerateEd25519Async(String^ comment);   // openssh-key-v1 未加密
  static IAsyncOperation<IBuffer^>^ GenerateRsaAsync(int bits, String^ comment);
  static IAsyncOperation<KeyInfo^>^ InspectAsync(const Array<uint8>^ privateKey, String^ passphrase); // 解析失败 nullptr
};
// SftpSession / Forwarder 见 F01 / F03 任务
```

### 6.3 错误码（C# `SshErrorCode` 与 `native/core/ssh/error_codes.h` 数值完全一致）

沿用鸿蒙端码表：`0` 无错误；`1xx` 连接与网络（101 DNS、102 连接超时、103 拒绝、104 不可达）；`2xx` 认证（201 密码、202 公钥、203 KI、204 私钥短语/加载、205 认证超时、206 本机无凭据）；
`3xx` 协商与主机密钥（301 算法、302 未知主机密钥、303 主机密钥不匹配、304 握手失败、305 握手超时）；`4xx` 会话（401 远端关闭、402 会话超时、403 socket 错误、404 keepalive 超时、405 策略性断开）；`5xx` 内部；`6xx` SFTP（F01 定义具体值）；`999` 未知。
每个码在 `Strings/*/Resources.resw` 有中文与英文文案（键 `Error_<数值>`）。`scripts/check-error-codes.ps1` 在门禁中对拍两份码表。

### 6.4 内存与安全纪律

- 密码、短语、私钥缓冲用完 `OPENSSL_cleanse`；C# 侧 `byte[]` 用完 `Array.Clear`（string 无法清零，尽量缩短存活）。
- 会话对象析构顺序：停 I/O 线程 → 关 channel → 关 session → 关 socket → 释放 grid；WinRT 对象被 GC 前必须显式 `Close()`（SessionManager 负责）。
- 所有跨线程访问 grid 用 `std::mutex`，`CopyDirtyRows` 持锁时间 < 1 ms。

---

## 7. 终端子系统

### 7.1 单元格布局（沿用鸿蒙端，16 字节/格，小端）

| 偏移 | 类型 | 含义 |
|---|---|---|
| 0 | u32 | Unicode 码点（0 空；`0xFFFFFFFF` 宽字符续格） |
| 4 | u32 | 前景 ARGB（已解析调色板/真彩；默认色用特殊值 `0x00000001` 标记，由外观决定） |
| 8 | u32 | 背景 ARGB（默认色 `0x00000002`） |
| 12 | u16 | 属性位：bit0 bold、1 italic、2 underline、3 blink、4 reverse、5 strike、6 dim、7 wide、8 invisible |
| 14 | u16 | 保留 |

> 默认前景/背景用标记值而不是写死黑白，是鸿蒙端 TerminalRender 历史失败用例（「native 默认黑底替换为外观背景」）的教训：让外观切换无需重放数据。

### 7.2 帧调度器 `FrameScheduler`（App/Terminal）

- 单例；`Register(TerminalView)` / `Unregister`；`SetVisible(view, bool)`；`Wake()`（任意线程可调，内部封送）。
- 订阅 `CompositionTarget.Rendering`；每帧：对每个可见视图 `if (screen.Revision != view.LastRevision) view.PullAndInvalidate()`；光标闪烁按 530 ms 翻转触发重绘。
- 连续 30 帧无工作 → 退订；`Wake()` 再订阅。应用挂起、终端页隐藏 → 全部 `SetVisible(false)`。
- 纯逻辑部分（空闲计数、闪烁相位、可见集合）放 `Core/Terminal/FrameSchedulerCore`，可单测。

### 7.3 渲染器 `TerminalRenderer`（Win2D）

- 每个 `TerminalView` 持有一个 `CanvasRenderTarget`（尺寸 = cols×cellW, rows×cellH，DPI 跟随）作为「行缓存」，`CanvasControl.Draw` 时整张绘出 + 叠加光标与选区。
- 脏行重绘：按行把连续同属性的格合并成 run → 先 `FillRectangle` 背景 run，再 `DrawText` 文本 run（`CanvasTextFormat` 等宽、`WordWrapping.NoWrap`）；宽字符单独按 2 格宽定位居中绘制。
- 整屏滚动（大量输出）时 90% 行都脏，直接整张重绘即可；不做位块搬移优化（W10M Win2D 自拷贝需双缓冲，收益待 Q01 实测再决定）。
- 设备丢失：处理 `CreateResources` 与 `CanvasDevice.DeviceLost`，重建行缓存并全量重绘。
- 属性：bold 用粗体字重（字体无粗体则描边加粗），bold-as-bright 可选；underline/strike 画线；dim 降 alpha；reverse 交换前景背景；invisible 不画字。

### 7.4 字体与度量

- 随包 JetBrains Mono Regular/Bold（`ms-appx:///Assets/Fonts/JetBrainsMono-Regular.ttf#JetBrains Mono`）。
- CJK：W10M 中文系统字体回退——**SP04 真机实测 `Microsoft YaHei UI` 可用**（2026-09-18，Lumia 950 / 10.0.15254.603，经 `CanvasFontSet` 枚举确认）；宽字符强制按 2×cellW 定位，字形超宽时缩放到格内。
- 度量：用 `CanvasTextLayout` 测 `"M"` 宽与行高 → `cellW = ceil(advance)`、`cellH = ceil(lineHeight × lineHeightFactor)`，字号或 DPI 变化时重测。
- `cols = floor((viewWidth - 2×padding) / cellW)`，`rows = floor((viewHeight - 2×padding - keyBarHeight(若覆盖)) / cellH)`；变化后 **防抖 100 ms** 调 `SshSession.Resize`。
- 字号范围 8–28 epx，默认 12（竖屏约 48 列、横屏约 88 列）。

### 7.5 输入

**软键盘（SoftKeyboardInput）**
- 页面内放一个 1×1、Opacity=0.01 的 `TextBox`（`IsSpellCheckEnabled=false`、`IsTextPredictionEnabled=false`、`InputScope=Default`/可设为 `Url` 减少自动大写）；点终端区域 → `Focus(Programmatic)` 弹出 SIP。
- **哨兵文本法**：TextBox 始终保留哨兵字符串 `"\u200B\u200B"`，光标在末尾；
  `TextChanged` 时与哨兵比较：多出的字符 → 发送；少了 → 按少掉的个数发 `DEL(0x7F)`；然后复位为哨兵。
- **组合态**：`TextCompositionStarted` → 标记组合中，期间 `TextChanged` 不发送；`TextCompositionEnded` → 取最终提交文本发送并复位。（SP05 验证 Word Flow 拼音是否触发这些事件；不触发则用「文本含未确认下划线段」无法判定 → 退化为「延迟 300 ms 未再变化才发送」。）
- 回车：`KeyDown(VirtualKey.Enter)` → 发 `\r`，`e.Handled = true`。
- `InputPane.Showing/Hiding`：取 `OccludedRect.Height`，终端可视高度减去遮挡与键条高度并 `EnsuredFocusedElementInView = true` 阻止页面整体上推。

**键条（KeyBar）**：见 UI §5.6；按键产生 `KeyChord`（键 + 修饰），经 `StickyModifiers`：单击 Ctrl → 下一个键带 Ctrl 后自动释放；双击或长按 → 锁定直到再次点击。

**物理键盘（HardwareKeyboardInput，Continuum/蓝牙键盘）**
- `CoreWindow.KeyDown`（非字符键：方向、F1–F12、Home/End/PgUp/PgDn/Insert/Delete、Esc、Tab、Enter、Backspace）
- `CoreWindow.CharacterReceived`（可打印字符，含 IME 结果）
- `Dispatcher.AcceleratorKeyActivated`（Alt 组合与 F10 等系统键）
- 有物理键盘活动时不唤起 SIP（TextBox 失焦，焦点放在 TerminalView 上，`IsTabStop=true`）。

**键位映射（Core/Terminal/KeyMap）** —— 纯函数 `byte[] Map(KeyChord chord, TerminalModes modes)`：
- 方向键：普通 `ESC [ A`，应用光标模式（DECCKM）`ESC O A`；带修饰 `ESC [ 1 ; m A`（m = 1 + shift + 2·alt + 4·ctrl）
- Home/End：`ESC [ H` / `ESC [ F`（应用模式 `ESC O H/F`）；PgUp/PgDn `ESC [5~` / `ESC [6~`；Insert `ESC [2~`；Delete `ESC [3~`
- F1–F4 `ESC O P..S`；F5 `ESC [15~`，F6 `17~`，F7 `18~`，F8 `19~`，F9 `20~`，F10 `21~`，F11 `23~`，F12 `24~`
- 带修饰的 Home/End/~/F 键（xterm 惯例）：Home/End 与 F1–F4 用 `ESC [ 1 ; m H/F/P..S`，PgUp/PgDn/Insert/Delete 与 F5–F12 用 `ESC [ n ; m ~`（m 公式同方向键；无修饰时才用上一条的 SS3/短形式）
- Ctrl+字母 → 字母 & 0x1F；Ctrl+Space/Ctrl+@ → 0x00；Ctrl+[ → ESC；Ctrl+\ → 0x1C；Ctrl+] → 0x1D；Ctrl+^ → 0x1E；Ctrl+_ → 0x1F
- Alt+X → `ESC` + X（CSI/SS3 路径已把 alt 编码进 m，不再加前缀）；Backspace → `0x7F`（可设置为 `0x08`）；Enter → `\r`；Tab → `\t`；Shift+Tab → `ESC [ Z`

### 7.6 指针与手势（PointerInput）

| 输入 | 普通屏幕 | Alt-screen（vim/less/htop） | 远端开启鼠标上报 |
|---|---|---|---|
| 单指上下拖动（触摸） | 滚动回滚缓冲（带惯性） | 发方向键 ↑/↓（每 cellH 一次，可设置改为滚轮序列） | 发滚轮序列 |
| 双指捏合 | 实时改字号，松手保存到当前外观/全局字号 | 同左 | 同左 |
| 长按（Holding） | 进入选择模式，选中长按处单词，出现两端拖柄 | 同左 | 同左（选择优先） |
| 单击 | 聚焦 + 弹键盘；选择模式下退出选择 | 同左 | 发点击序列 |
| 鼠标左键拖动 | 选择文本 | 选择文本 | 发按下/拖动/释放序列（Shift+拖动强制本地选择） |
| 鼠标双击/三击 | 选词/选行 | 同左 | 同左 |
| 滚轮 | 回滚 3 行/格 | 方向键 | 滚轮序列（SGR：`ESC [ < 64/65 ; x ; y M`） |
| 右键 | 菜单：复制/粘贴/全选/清屏/发送片段 | 同左 | 同左 |

选择模型（Core/Terminal/SelectionModel）：以 (绝对行号含回滚偏移, 列) 表示锚点与焦点；宽字符边界自动扩展；复制时行尾空白裁剪、软换行不插入换行（需 native 提供行续接标记，T01 在保留位记录）。

### 7.7 粘贴

读 `Clipboard.GetContent()` 文本 → 统一换行为 `\r` → 若含换行且长度 > 1 行：弹「多行粘贴确认」（显示行数与前 5 行预览，勾选「以后不再提示」）→ 若远端开启 bracketed paste，包裹 `ESC[200~ … ESC[201~` → 分块 4 KB 写入。

---

## 8. 数据模型与存储

### 8.1 实体（Core/Models）

图例：☁ = 进同步文档；🏠 = 本机专有（不上云，下行保留）

**Host**
| 字段 | 类型 | 同步 | 约束 / 说明 |
|---|---|---|---|
| id | string | ☁ | UUID v4 小写；改名不改 id；兼容桌面端非 UUID 的历史 id（只要求非空） |
| name | string | ☁ | 1–255 |
| host | string | ☁ | 1–1024 |
| port | int | ☁ | 1–65535，默认 22 |
| username | string | ☁ | **1–255（必填，对端 schema 要求）** |
| authType | `password`/`key`/`agent` | ☁ | agent = 应用内 agent |
| hostFingerprint | string | ☁ | ≤255，`SHA256:...`；TOFU 写入 |
| keepalive | int | ☁ | 0–3600 秒，默认 30 |
| groupId | string? | 🏠 | 主机归属分组（桌面端分组是隧道分组，主机无分组） |
| keyId | string? | 🏠 | authType=key 时引用 KeyEntry |
| appearanceId | string? | 🏠 | null = 用全局默认外观 |
| jumpHostId | string? | 🏠 | ProxyJump |
| initCommands | string[] | 🏠 | 连接后依次发送 |
| envVars | map | 🏠 | `setenv` 请求（服务器 AcceptEnv 才生效） |
| termType | string | 🏠 | 默认 `xterm-256color` |
| tmuxAutoAttach | bool | 🏠 | 默认 false |
| tmuxSessionName | string | 🏠 | 空 = `main` |
| backspaceSendsCtrlH | bool | 🏠 | 默认 false |
| sortOrder | int | 🏠 | 组内排序 |
| lastConnectedAt | string? | 🏠 | ISO 时间，用于「最近使用」 |

**HostGroup**：`id` ☁、`name` ☁（1–255）、`color` ☁（必填 `#RRGGBB`，默认 `#4F8CFF`）、`order` 🏠、`collapsed` 🏠。
> 桌面端的分组挂在隧道上（`tunnel.groupId`），本应用同时用于主机归属（`host.groupId` 🏠）；下行删除分组时，本机主机的 `groupId` 置空而非删除主机。

**Tunnel**（与桌面端 `TunnelConfig` 同构，避免鸿蒙端那种透传缓存）
| 字段 | 同步 | 说明 |
|---|---|---|
| id、name(1–255)、serverId、groupId?、type(`local`/`remote`/`dynamic`/`relay`)、listenHost(≤1024)、listenPort(1–65535)、destHost(≤1024)、destPort(0–65535)、destServerId(≤255，relay 必须指向存在的主机)、autoReconnect、enabled | ☁ | 字段语义见桌面端 `src/shared/types.ts` |
| autoStart | 🏠 | 应用启动后自动开启（手机默认 false） |

**KeyEntry** 🏠：`id`、`name`、`keyType`（ssh-ed25519/ssh-rsa/ecdsa-sha2-nistp256…）、`bits`、`format`（openssh/pem）、`encrypted`、`publicKeyOpenSsh`、`fingerprintSha256`、`createdAt`、`comment`。私钥内容存 SecureStore。

**Snippet** 🏠：`id`、`name`、`content`、`groupName`（简单文本分组）、`sortOrder`、`sendEnter`（发送后是否追加回车）。

**AppearanceProfile** 🏠：`id`、`name`、`builtIn`、`fontFamily`、`fontSize`(8–28)、`lineHeight`(1.0–1.6)、`fontWeightBold`(bool 用粗体)、`boldAsBright`、`cursorStyle`(block/bar/underline)、`cursorBlink`、`padding`(0–16)、`palette`(16×`#RRGGBB`)、`foreground`、`background`、`cursor`、`selection`。
> 内置外观由代码定义（A02 `BuiltInThemes`，id 固定 `builtin-<slug>`），只读、**不写入** `appearances.json`；有效外观解析链（A01）：主机 `appearanceId` → 全局 `defaultAppearanceId` → 内置默认。

**KnownHost** 🏠：`id`、`host`、`port`、`keyType`、`fingerprintSha256`、`addedAt`、`lastSeenAt`。
> 与 Host.hostFingerprint（☁）的关系：连接时优先比对 `KnownHost(host,port)`；无记录但 Host 上有指纹（来自同步）时，以同步指纹做校验并在匹配后写入 KnownHost。

### 8.2 存储介质

| 数据 | 位置 | 格式 |
|---|---|---|
| 主机/分组/隧道/片段/外观/已知主机/密钥元数据 | `LocalFolder/data/{hosts,groups,tunnels,snippets,appearances,knownhosts,keys}.json` | `{ "schemaVersion": n, "items": [...] }`，原子写 |
| 设置 | `ApplicationData.LocalSettings`（单值 < 8 KB） | 类型化访问器 + 默认值表 |
| 凭据与同步敏感状态 | `LocalFolder/secure/secrets.bin` | `DataProtectionProvider("LOCAL=user")` 加密的 JSON 键值表 |
| 同步保险库缓存（VaultCacheState，含 baseDocument） | `LocalFolder/secure/vault-cache.bin` | 同上（体积可达 2 MiB，单独文件） |
| 认证状态（AuthStore） | `LocalFolder/secure/auth.bin` | 同上 |
| 日志 | `LocalFolder/logs/app.log`，1 MiB × 3 轮转 | 纯文本，脱敏 |
| 会话恢复快照（打开的会话、窗格树） | `LocalFolder/state/sessions.json` | 仅 id 与布局，不含内容 |

**SecretStore 键名规范**：`host:<hostId>:password`、`host:<hostId>:passphrase`、`key:<keyId>:private`、`key:<keyId>:passphrase`。
（同步密码不保存；保险库密钥保存在 `vault-cache.bin`，与桌面端 `vault-cache.ts` 一致。）
删除主机/密钥必须级联删除对应键。

### 8.3 设置键（D04 实现，`SettingsRepository`）

| 键 | 类型 | 默认 | 说明 |
|---|---|---|---|
| `themeMode` | string | `dark` | system/dark/light |
| `defaultAppearanceId` | string | `builtin-harmony-dark` | |
| `terminalFontSize` | int | 12 | 全局字号（外观未覆盖时） |
| `keepScreenOn` | string | `session` | never/session/always |
| `keepAliveInBackground` | bool | true | 申请 ExtendedExecution |
| `backgroundDisconnectMinutes` | int | 0 | 后台 N 分钟后主动断开，0 = 不主动 |
| `hapticsEnabled` | bool | true | |
| `keyBarLayout` | string | `esc,tab,ctrl,alt,up,down,left,right,home,end,pgup,pgdn,pipe,slash,minus,tilde,paste` | 逗号分隔键 id |
| `keyBarVisible` | bool | true | |
| `pasteConfirmMultiline` | bool | true | |
| `altScreenScroll` | string | `arrows` | arrows/wheel |
| `scrollbackLines` | int | 5000 | 1000–50000 |
| `shortcuts` | string | JSON | 快捷键表覆盖项 |
| `hostGroupCollapsed` | string | JSON | 分组折叠状态 |
| `hostSortMode` | string | `name` | name/recent |
| `useSystemAccent` | bool | true | 强调色取系统强调色 |
| `showQuickConnect` | bool | true | 主机页快速连接卡 |
| `language` | string | `system` | system/zh-CN/en-US（重启生效） |
| `connectTimeoutSeconds` | int | 15 | 5–60 |
| `reconnectMaxAttempts` | int | 6 | 0–10，0 = 不自动重连 |
| `agentKeyTimeoutMinutes` | int | 15 | 应用内 Agent 密钥保留时间 |
| `syncPollForegroundSeconds` | int | 60 | 0 = 关闭轮询 |
| `lastVersionSeen` | string | "" | 升级提示 |
| `logLevel` | string | `info` | debug/info/warn/error |

### 8.4 仓库与迁移

- `JsonStore<T>`：`LoadAsync` → 读文件 → 按 `schemaVersion` 依次跑迁移函数 → 反序列化（手写 `IEntityCodec<T>`）；`SaveAsync` 写 `*.tmp` 再替换；读坏文件时备份为 `*.corrupt-<时间>` 并以空集合启动 + 记日志 + UI Banner 提示。
- 每个 Repository：内存列表 + `GetAll/GetById/Add/Update/Remove/ReplaceAll`，写操作串行化（`SemaphoreSlim`）、保存后触发 `Changed` 事件（带 `ChangeOrigin.User|Sync`，**来源为 Sync 时不触发同步标脏**，防止回环）。

---

## 9. 会话管理与连接流程

### 9.1 SessionInfo（可观察）

`SessionId`（应用层稳定 id，重连不变）、`HostId`（临时快速连接为 null）、`Title`、`State`（UI 态：`Connecting`/`Authenticating`/`Connected`/`Reconnecting`/`Disconnected`/`Error`/`Closed`）、`ErrorCode`、`ErrorMessage`、`ReconnectAttempt`、`ReconnectInSeconds`、`NativeSession`（可替换）、`ConnectedAt`。

### 9.2 连接时序

```
UI 点击主机 → SessionManager.Open(hostId, mode)
 1. 创建 SessionInfo(Connecting) → 导航/切换到终端页（先出画面，蒙层显示“正在连接”）
 2. native.ConnectAsync(options)
      └ HostKeyCheck 事件：
          KnownHost 有记录：一致 → Accept；不一致 → Reject → 错误 303 → HostKeyMismatchDialog（只允许“查看详情/取消”，
                                     另提供“移除旧指纹后重新连接”需二次确认）
          无记录：Host.hostFingerprint 非空且一致 → Accept 并写 KnownHost；否则弹 HostKeyDialog（指纹 + randomart，“信任并连接/取消”）
 3. 认证（按 authType）：
      password：SecretStore 有 → 用；无 → CredentialDialog（密码 + “记住”）→ 失败 201 → 重新弹框（最多 3 次）
      key：读 KeyEntry 私钥 +（加密则短语：SecretStore 或 PassphraseDialog）→ 失败 202/204 → 提示
      agent：应用内 agent 已解锁的密钥逐个尝试
      服务器要求 keyboard-interactive：AuthPrompt 事件 → KbdInteractiveDialog（支持 2FA 验证码）
 4. OpenShellAsync(cols, rows) → State=Connected → 触感轻震 → AutoRun（tmux 附着命令 → initCommands）
 5. 首次成功：Host.hostFingerprint 为空时写入并保存（触发同步），lastConnectedAt 更新（🏠 不触发同步）
```

### 9.3 断线与重连

- native `Disconnected` 且非用户主动关闭 → `Reconnecting`：退避 1→2→5→10→20→30 s，默认最多 6 次（设置可改）；蒙层显示倒计时与「立即重连 / 取消」。
- 重连：新建 native 会话替换 `NativeSession`，`SessionId` 与窗格绑定不变；旧会话迟到事件按句柄比对丢弃。回滚内容保留（新 shell 输出追加）。
- 认证类错误（2xx）、主机密钥错误（303）不自动重连。
- 用户关闭会话：`Close()` → 移除 SessionInfo → 窗格树删叶子。

### 9.4 AutoRun（Core 纯函数）

`IList<string> BuildAutoRunCommands(Host host)`：
1. `tmuxAutoAttach` → `tmux new-session -A -s <name>`（名字按 shell 规则转义；空名用 `main`）
2. `initCommands` 逐条（空行跳过）
每条追加 `\r` 发送；日志只记条数。

---

## 10. 生命周期与后台保活（Windows 10 Mobile）

| 场景 | 行为 |
|---|---|
| 终端页可见 + 有活跃会话 | `keepScreenOn=session`：`DisplayRequest.RequestActive()`；离开终端页或无会话 → `RequestRelease()`（必须成对） |
| 应用进入后台（`EnteredBackground`/窗口 `VisibilityChanged=false`）且有会话 | 若 `keepAliveInBackground`：请求 `ExtendedExecutionSession(Reason=Unspecified)`；成功 → 会话继续；被拒 → 进入「宽限」：立即对每个会话保存状态并策略性断开（错误码 405，保留 SessionInfo） |
| `ExtendedExecutionSession.Revoked` | 同「被拒」：策略性断开 |
| `Suspending` | 用 deferral 保存 `sessions.json` 与仓库未落盘数据；未断开的会话标记「挂起前在线」 |
| `Resuming` / 回到前台 | 对「挂起前在线」或 405 断开的会话：已不可用 → 立即重连；仍 Established → `ProbeNow()`（5 s 内无入站则判断线走重连） |
| 冷启动（进程被杀后再开） | 读 `sessions.json` → 在「会话」页显示「上次打开的会话」卡片，一键全部恢复（不自动连，避免意外耗流量） |
| 网络变化（`NetworkInformation.NetworkStatusChanged`） | 退避中的会话立即重连；已连接的会话 `ProbeNow()`；同步协调器在网络恢复时触发一次同步 |
| 省电模式（`PowerManager.EnergySaverStatus == On`） | 终端页顶部 Banner 提示「省电模式下后台连接会被系统断开」 |
| 后台 N 分钟主动断开 | `backgroundDisconnectMinutes>0` 时计时到期策略性断开，省电 |

> 真正「断了也不丢现场」的手段仍是 **tmux 自动附着**（主机编辑页「高级」里默认建议开启，并解释原因）。

---

## 11. SFTP、端口转发、跳板

### 11.1 SFTP（F01/F02）

- 复用已认证的 SSH 连接新开 SFTP 子系统（native `SftpSession` 挂在 `SshSession` 上），不重复认证。
- 能力：列目录（名称/大小/权限/mtime/类型/符号链接目标）、进入/返回、新建目录、重命名、删除（目录递归需确认）、chmod、下载（`FileSavePicker` 或「下载」文件夹）、上传（`FileOpenPicker` 多选）、传输队列（进度、速率、取消、失败重试）、断点续传（按远端/本地已写大小续写）。
- 传输在 native 线程分块 32 KiB，进度事件节流 200 ms。

### 11.2 端口转发（F03/F04）

| 类型 | 实现 | 手机上的可用性说明（UI 如实展示） |
|---|---|---|
| local（-L） | native 监听 `listenHost:listenPort`（Winsock `bind/listen`），每个入站连接开 `direct-tcpip` | 同一台手机上**其他应用无法连本应用的 127.0.0.1**（UWP 回环隔离）；可服务局域网内其他设备（listenHost=0.0.0.0，需 privateNetworkClientServer） |
| remote（-R） | `libssh2_channel_forward_listen_ex` → 接受远端通道 → 连本机/局域网目标 | 目标为手机可达地址 |
| dynamic（-D） | 本地监听 + SOCKS5 握手（Core 移植桌面端 `socks5.ts` 解析逻辑）→ `direct-tcpip` | 同 local 限制 |
| relay | 不运行 | 可查看、编辑、同步；列表中显示「仅桌面端运行」 |

运行态：`idle/connecting/running/reconnecting/error` + 统计（活跃连接数、累计、上下行字节、速率），与桌面端 `TunnelStatus` 一致。隧道运行时复用或新建该主机的 SSH 连接（无终端）。

### 11.3 ProxyJump（F05）

`Host.jumpHostId` 🏠 指向跳板主机，可链式。实现：先建立跳板会话 → 开 `direct-tcpip` 到目标 host:port → 通过 `libssh2_session_callback_set(LIBSSH2_CALLBACK_SEND/RECV)` 把该通道作为目标会话的传输层。任一级断开，下级收到 403/401 并进入重连流程（重连从最上级开始）。环检测：保存时拒绝形成环。

---

## 12. 安全

### 12.1 凭据

- 密码/短语/私钥只在 SecretStore（DPAPI 加密）与内存中短暂存在；ViewModel 不长期持有明文（对话框返回后立即交给 SessionManager 并清空字段）。
- 应用内 agent：解锁后的私钥在 native 内存保存，默认 15 分钟无使用自动清除，应用挂起时清除。

### 12.2 日志脱敏

`ILogger` 实现统一过「脱敏器」：遇到 `password`、`passphrase`、`token`、`privateKey`、`recoveryKey`、`ciphertext`、`Authorization` 等键名或 `-----BEGIN`、`SPM1-` 模式时整值替换为 `***`。Q03 以单测 + 抽查日志文件验收。

### 12.3 同步通道明文 HTTP 风险

端到端加密保护**文档内容**，不保护**登录邮箱/密码、access/refresh token、保险库信封**。当前服务端为公网裸 IP + HTTP：
- 登录/注册页固定显示风险提示；不提供「记住账号密码」自动登录（token 由 refresh 维持）。
- 建议服务端挂域名 + TLS 后把 `SyncApiBaseUrl` 切 https（配置项，不改代码）。
- W10M 的 TLS 根证书较旧（2017 年），若日后切 HTTPS 使用 Let's Encrypt（ISRG Root X1）需验证手机信任链，必要时在 manifest `Certificates` 声明中打包根证书（Q03 验证）。

---

## 13. 测试策略与质量门禁

| 层 | 手段 | 命令 / 位置 |
|---|---|---|
| Core 纯逻辑 | xUnit：模型校验、仓库（内存 IFileSystem）、KeyMap、选择、窗格树、AutoRun、片段模板、SOCKS5 解析、**同步序列化/合并/协调器/API 客户端（假传输）** | `dotnet test tests/SshTool.Core.Tests` |
| 跨端同步向量 | Node 脚本调用桌面端同款算法（hash-wasm + node:crypto）生成：信封、文档密文、恢复密钥；C++ 与 C# 测试读取断言互解 | `tools/sync-vectors/generate.mjs` → `native/tests/vectors/*.json` |
| native 核心 | GoogleTest（移植鸿蒙端用例 + 新增 keytool/sftp/fwd）；集成测试连宿主机 WSL `sshd`（可选，环境变量开启） | `cmake --build native/tests/build && ctest` |
| App 构建 | x64 Debug 构建必须通过；ARM Release（.NET Native）在里程碑出口构建 | `msbuild` 见 `scripts/verify.ps1` |
| 真机 | 每个里程碑的 📱 验收清单（`04-TASKS.md` 末尾） | Lumia 950 |
| 互通 | 同账号：桌面端 ↔ Lumia ↔ 鸿蒙端 场景清单（`03-SYNC-PROTOCOL.md §10.3`） | 手工 |

**门禁（`scripts/verify.ps1` 全绿才能勾任务）**：Core 测试全过；native 测试全过；错误码对拍一致；App x64 Debug 构建成功；无新增编译警告（C# `TreatWarningsAsErrors` 对 Core 开启）。

---

## 14. 风险登记

| # | 风险 | 概率 | 影响 | 应对 / 触发的 Spike |
|---|---|---|---|---|
| R1 | ~~OpenSSL 3.x 无法编成 ARM32 UWP 静态库或链接报 UWP 禁用 API~~（SP02 已排除：3.6.3 编成并链接成功，2026-09-17） | ~~中~~ 已排除 | — | libssh2 链接 UWP 禁用 API 的风险由 SP03 继续验证；原兜底（1.1.1w / 方案 B SSH.NET）保留至 SP03 结论 |
| R2 | libssh2 在 UWP 中调用了被禁 API（如 `GetUserName`、非 UWP 注册表函数） | 中 | 编译或 WACK 失败 | SP03 编进组件跑一次真实连接；以宏打补丁记录在 `native/third_party/PATCHES.md` |
| R3 | Win2D 在 Lumia 上终端帧率不足 | 中 | 体验差 | SP04 实测；兜底见 D5 |
| R4 | Word Flow 中文输入法组合事件不可用，导致中文输入重复/丢字 | 中 | 中文用户体验差 | SP05；兜底：延迟提交 + 「输入框模式」（弹出单行编辑框整句发送） |
| R5 | W10M 上 `DataProtectionProvider("LOCAL=user")` 行为异常 | 低 | 凭据存储方案变更 | SP06；兜底 PasswordVault |
| R6 | ExtendedExecution 在 W10M 几乎总被拒/很快撤销 | 高 | 后台必断 | 设计上已接受：优雅断开 + 回前台重连 + tmux 附着；文案告知 |
| R7 | .NET Native Release 构建与 Debug 行为不一致（反射、泛型实例化缺失） | 中 | 真机崩溃 | 禁用反射序列化；每个里程碑出口必须 ARM Release 真机冒烟 |
| R8 | 同步格式偏差导致桌面端整份拒绝解析 | 中 | 三端同步中断、甚至覆盖 | 严格 writer + 与桌面端 zod 规则逐条对齐的校验器 + 用桌面端真实导出的文档做回归夹具；下行解析失败**只报错不上传** |
| R9 | 用户同一账号在旧版鸿蒙端（未完整支持 tunnel/group 颜色）写入 | 低 | 字段被冲 | 文档合并按字段；本端完整承载全部字段 |
| R10 | VS2022 不支持 W10M 部署 | 中 | 调试不便 | 以 VS2019 为主（X01 明确） |
| R11 | ARM32 工具链在新 Windows SDK 被移除 | 已知 | 构建失败 | SDK 固定 ≤ 19041/17763，`Directory.Build.props` 锁定 |
| R12 | 公网明文 HTTP | 高 | 账号凭据可被窃听 | §12.3 |

---

## 15. 性能与资源预算（Q01 验收）

| 指标 | 目标 |
|---|---|
| 冷启动到主机列表可交互 | < 2.5 s（Release/.NET Native） |
| 点击主机到出现提示符（局域网） | < 2 s（不含认证交互） |
| `cat` 1 MB 文本 | 全程 ≥ 30 fps，无卡死；完成后 CPU 回落 |
| 静止终端 | 无帧回调订阅（CPU ≈ 0） |
| 按键回显 | < 50 ms + RTT |
| 100 台主机列表滚动 | 无明显卡顿（虚拟化 ListView） |
| 内存 | 单会话 < 120 MB；4 会话 + 各 5000 行回滚 < 250 MB |
| Argon2id 解锁保险库 | 有进度提示；< 8 s |
