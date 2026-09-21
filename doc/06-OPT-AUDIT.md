# 代码优化审查报告（O 系列工作包的依据）

> 审查日期：2026-09-21
> 审查基线：`main` @ 9e180ed（工作区干净），111 个任务完成 106 个
> 对照文档：`01-DESIGN.md`（§6 原生层 / §7 终端子系统 / §13 门禁 / §15 预算）、
> `03-SYNC-PROTOCOL.md`、`05-CODE-AUDIT-UI-REDESIGN.md`、`PERF-REPORT.md`
> 方法：App / Core / Native 三层逐文件核查，每条结论都落到 `文件:行` 的代码证据

---

## 1. 结论先行

`05-CODE-AUDIT-UI-REDESIGN.md` 提出的基础设施**已经建成且质量不差**：
`Common/NavigationLifetime.cs`（导航世代）、`Common/TaskExtensions.Forget`（fire-and-forget 观察点）、
`Mvvm/AsyncCommand`、`SettingsPage` 的 150 ms Slider 去抖、Native 的 `diag_counters` 资源计数
都已落地；Core 的异步纪律（339 处 `await` / 340 处 `ConfigureAwait(false)`）、
`ApiClient` 的重试与单飞刷新、`Cell` 的 16 字节定长布局、RAII 与错误路径释放，经核查均无问题。

问题出在**覆盖率与收尾**，以及三处 doc 明确写了纪律、实现却没跟上的地方：

1. **两条确认的内存泄漏路径**（页面级视图/VM 订阅应用级单例后从不解除），
   直接对着 `05` §10 仍未勾选的「进入退出终端 50 次无事件倍增和明显内存增长」；
2. **一条无人消费却持续累积的 native 输出缓冲**，威胁 §15「单会话 < 120 MB」与 Q02 长稳；
3. **Q04 的硬编码文案门禁本身失效**，导致 `05` §10 的「可见文案全部资源化」被错误勾成 `[x]`。

其余为纯性能/分配优化，按收益排序见 §3。

---

## 2. 文档订正（按 `README.md` 工作流第 6 条：先改文档）

`05-CODE-AUDIT-UI-REDESIGN.md` §10 中与实测不符、本轮已改回 `[ ]` 的条目：

| 原条目 | 实测证据 |
|---|---|
| 页面级订阅均有对应解除 | `ViewModels/HostListViewModel.cs:65-68`、`MainViewModel.cs:63`、`SettingsViewModel.cs:77` 等 9 处订阅应用级单例，全文件 `-=` 计数为 0 |
| fire-and-forget 异常全部可观察 | 48 处 `var ignore = …`（非 Debug）对 37 处 `.Forget(`，两种写法并存 |
| 可见文案全部资源化，中英文不混排 | 非 Debug XAML 仍有 137 处中文字面量；C# 中对 `.Text/.Title/.Content/…` 赋中文 37 处 |

同时**已确认过时、不要再改**的条目（`05` §3 的证据行号已失效）：

- **C-01 已修**：`Views/TerminalPage.xaml.cs:181/195` 与 `ViewModels/TerminalViewModel.cs:129/143`
  已改为具名处理器 `OnBoundSessionChanged` / `OnAttachedSessionChanged`，`+=`/`-=` 对称，
  且有显式 `Detach()`。`ViewModels/TunnelsViewModel.cs:206` 是同一正确范式。
- **C-02 已实现，但形式与文档建议不同**：落地的是 `NavigationLifetime` 的**整型世代号**
  （`Begin()`/`IsCurrent(gen)`/`End()`），不是文档里写的 `CancellationTokenSource`；
  `Views/*.xaml.cs` 中无任何一处用 CTS 做导航失效判定。文档应把「整型世代号」记为既定范式。
- **C-07 已完成一半**：`Views/SettingsPage.xaml.cs:35,54,73,279` 的 150 ms 去抖 +
  `ManipulationCompleted` 立即提交已实现；缺的是 Repository 侧的相等值短路（见 O02）。

---

## 3. 发现清单

### P0-1 · `pendingOutput_`：每会话一条无人消费、无上限的输出累积链（O01）

- `src/SshTool.Native/Bridge/SshSession.cpp:306-320` — `OnShellData` 每次都
  `lock(outputMutex_); pendingOutput_ += data;`，然后才 `screen_->Feed(...)`。
- 唯一消费者 `FetchPendingOutput()`（`SshSession.cpp:787`）**全仓无生产调用方**：
  `Platform/NativeSshSession.cs:205` 与 `Core/Sessions/ISshSession.cs:58` 只是透传，
  真正的调用只出现在 `tests/SshTool.Core.Tests/Fakes/FakeSshSession.cs`。
  `SshSession.h:28` 自己写着「FetchPendingOutput 是过渡取数 API，T03 TerminalScreen 接管后移除」——
  T03 早已接管（渲染走 `TerminalScreen.Revision` + `CopyDirtyRows`），API 没删。
- 影响一（内存）：shell 通道存活期间**远端 stdout 的每个字节被永久累积**。
  `cat` 1 MB 即 +1 MB 常驻；`yes | head -n 200000`、`tail -f`、Q02 的 4 h 长稳会单调上涨。
  正对着 §15「单会话 < 120 MB」与 Q02「100 轮后 ≤ 基线 +10%」。
- 影响二（CPU）：每次 32 KB 通道读多一次互斥量 + `std::string` 扩容拷贝。
- 影响三（契约）：`dirtyCoalescer_.markConsumed()` 只在 `FetchPendingOutput` 里调用
  （`SshSession.cpp:794`）。没人调用 → 标志永久停在 `true` → `ContentDirty`
  **每个会话一生只触发一次**（`native/core/bridge_logic/dirty_coalescer.h:24-30`）。
  生产渲染是轮询式（§7.2）所以不炸，但 `Views/Debug/DebugConnectPage.xaml.cs:228` 的调试路径已名存实亡。

### P0-2 · 页面级视图订阅应用级单例后从不解除（O03）

两条独立路径，都在最常访问的页面上按「每次返回 +1」累积：

1. **视图侧**：`Views/Main/SessionsPivot.xaml.cs:30` 在 `Attach()` 里
   `vm.PropertyChanged += (s, e) => UpdateChrome();`。`vm` 是经
   `ServiceRegistry` 注册的**单例** `SessionsPaneViewModel`（`ViewModels/MainViewModel.cs:33-39`），
   而 `SessionsPivot` 随 MainPage 每次重建。匿名 lambda 无法解除 →
   单例的事件列表里每次访问多攥住一棵死掉的可视化树。
   `SessionList.ItemsSource = vm.Items`（同文件 :28）与 `Controls/SessionsPane.xaml.cs:21`
   的 `List.ItemsSource = vm.Items` 同理：单例 `ObservableCollection` 攥住已弃用 ListView 的
   CollectionChanged 订阅，TerminalPage 侧同样中招。
2. **VM 侧**：页面每次导航新建的 VM 在构造函数里订阅应用级单例，全文件 `-=` 为 0：
   - `ViewModels/HostListViewModel.cs:65-68`（`_hosts/_groups/_tunnels/_settings` 四个 `.Changed`）
   - `ViewModels/MainViewModel.cs:63`（`services.Sync.StateChanged`）
   - `ViewModels/SettingsViewModel.cs:77`（`_settings.Changed`）
   - `ViewModels/Sync/{AccountSync,SecurityRotate,VaultSetup,VaultUnlock}ViewModel.cs:61/51/42/39`

   `Views/MainPage.xaml.cs:26` 每次导航回来都 `new MainViewModel()`（无 `NavigationCacheMode`，
   全仓零处设置），于是**每访问一次首页就多一个永远活着、仍在响应仓库 `Changed`
   并触发 `RefreshAsync` 的死 VM**——既是泄漏，也是 `05` §1 描述的「返回后重复刷新」的直接成因。

正确范式仓内已有两例：`ViewModels/TerminalViewModel.cs` 的 `Detach()` 与
`ViewModels/TunnelsViewModel.cs:206`。

### P0-3 · `SettingsRepository.Set` 无相等值短路（C-07 的另一半，O02）

- `src/SshTool.Core/Storage/SettingsRepository.cs:63-76` — 校验后无条件
  `_store.Set(key, value)` 并无条件 `handler(this, new SettingChangedEventArgs(...))`。
- 页面侧虽有 150 ms 去抖，但键盘/点击调值仍会穿透，非 Settings 页的写入也不经去抖；
  更重要的是每次同值写都会扇出到外观重算与同步脏标记订阅方。
- 处理：`Set` 内先 `Read(def)` 取当前有效值，`Equals(current, value)` 则直接返回；
  补「同值 Set 不写 store、不触发 Changed」单测。

### P1-4 · `NavigationLifetime` 覆盖率 9/28（O04）

含 `OnNavigatedTo` 的页面 28 个，接入世代保护的只有 9 个（TerminalPage、HostEditPage、
SftpPage、KeysPage、KeyDetailPage、AppearanceListPage、AppearanceEditPage、SnippetEditPage、
TunnelEditPage）。未接入的 19 个里**包含全部 Sync 页**
（`Views/Sync/{AccountSync,Login,VaultSetup,VaultUnlock,SecurityRotate,ChangeLoginPassword,DeleteAccount,DeleteVault,SyncConflict}Page`）
以及 `SettingsPage`、`MainPage`、`SnippetsPage`、`KnownHostsPage`、`GroupManagePage`——
恰是网络 I/O 最长、用户最容易中途返回的页面。

### P1-5 · fire-and-forget 只收口了 1 个文件（O05）

48 处 `var ignore = …`（非 Debug）对 37 处 `.Forget(`；真正走 `Forget` 的只有
`ViewModels/HostListViewModel.cs`。密集处：`Views/Sync/AccountSyncPage.xaml.cs`（13 处）、
`Views/GroupManagePage.xaml.cs`（6 处）、`Views/HostEditPage.xaml.cs`（3 处）。
其中 `Infrastructure/DispatcherHelper.cs:92` 尤其值得注意：应用**自己的 UI 封送原语**
用 `var ignore = dispatcher.RunAsync(...)`，即别处依赖它做跨线程安全的那一层，
自己的派发失败无人观察。

### P1-6 · 每次 HTTP 请求新建 filter 与 client（O06）

`src/SshTool.App/Platform/UwpHttpTransport.cs:18-25` —
`using (var filter = new HttpBaseProtocolFilter()) { … using (var client = new HttpClient(filter))`。
`Windows.Web.Http` 的连接池挂在 filter 上，每请求新建即**每请求一次 TLS 握手**。
一个同步周期要打 revision/download/upload/devices/history 多个请求，
叠加 60 s 轮询与 3 s 去抖上传，在蜂窝网下是明显的延迟与耗电放大。
`Windows.Web.Http.HttpClient` 线程安全，可作为只读字段长期复用；
NoCache / NoCookies / `AllowAutoRedirect=false` 语义保持不变。

### P1-7 · 同步路径重复全文档序列化与深拷贝（O07，不改线上字节）

- `Sync/SyncCoordinator.cs:1106` 调 `SyncDocumentWriter.SameContent(local, pending.Document)`，
  而 `SameContent`（`Sync/Protocol/SyncDocumentWriter.cs:34-41`）对**两份**文档各跑一次
  `BuildJson`：3 次 `List` 复制 + 3 次排序 + 全字段 `JsonTextWriter`。
  同一份 `pending.Document` 在创建时（`SyncCoordinator.cs:1156`）已序列化过一次。
  → 在 `PendingUpload` 里缓存创建时的规范 JSON，`SameContent` 只重建 `local` 一侧。
- `Sync/SyncCoordinator.cs:1170` `Document = doc.Clone()` 对最多 5000 servers / 10000 tunnels /
  1000 groups 做整图深拷贝（`Sync/Protocol/SyncDocumentV1.cs:16-40`），
  紧接在刚做完的全量序列化之后。→ 先确认 `BuildLocalDocumentAsync()`（:1300）
  返回的是独占快照，是则可去掉这次 Clone。
- `Sync/SyncMerge.cs:16-58` — `ToCanonicalJson`（:69-72）把 base/local/remote 三份
  **序列化成字符串再 Parse 回 `JObject`**，合并后又 `merged.ToString()` +
  `SyncDocumentReader.Read()` 全量重解析重校验。2 MiB 文档上限下一次合并 4 次序列化 + 4 次解析，
  `JObject` 树约为原文的 3–5 倍内存。
  → 改「模型 → JObject」直转，跳过输入侧的字符串往返与排序/校验；
  **最后一次 `SyncDocumentReader.Read` 的全量校验必须保留**（`01-DESIGN` R8 的兜底）。

### P1-8 · `Repository<T>.FindIndex` 线性扫描在连接热路径上（O08）

`src/SshTool.Core/Storage/Repository.cs:277-287` 是 O(n) 循环，`GetByIdAsync`（:53-58）、
`AddAsync`（:72）等全部经它。热路径：`Sessions/SessionManager.cs:121`（每次连接）、
`:1237`（每次重连）、`:882`（密钥认证）。`SessionManager.FindKnownAsync`（:1281-1293）
按 host+port 线性扫，跳板链每跳扫一次（:581）+ 落盘时再扫（:754）。
→ `Repository<T>` 内维护 `Dictionary<string,int>`（id → 下标），`_items` 整体替换时重建；
`KnownHostRepository` 另加 `(host,port)` 索引。纯内存结构，不动持久化格式。

### P1-9 · Native 终端热路径：重复 CSI 解析 + 每 feed 全表扫描（O09）

- `native/core/term/vterm_screen.cpp:160-168, 420-506` — 每次 `feed()` 都用手写 CSI 扫描器
  （`csiPending_` 的 `append` + `erase(0, …)`，前删 O(n)）**再解析一遍全部字节**，
  只为跟踪 DECCKM / 1006 / 2004 三个模式位，而 libvterm 自身已在跟踪。
  → 从 `VTermState` 读取（或经已有的 `onSetTermProp`/DECSET 回调面），删掉手写扫描器。
- `vterm_screen.cpp:166, 400-418` — `refreshSoftWrapFlags()` 每次 `feed()` 无条件遍历全部行
  查 `vterm_state_get_lineinfo`。→ 只扫本次 damage rect 覆盖的行，或按 `VTERM_LINEINFO` 增量维护。
- 两项直接对应 `PERF-REPORT` §2 的「喂 x ms/帧」，是 `cat`/`yes`/`vim` 三个场景的稳定税。

### P1-10 · `TerminalRenderer` 每行每 run 的分配（O11）

- `src/SshTool.App/Terminal/TerminalRenderer.cs:243` → `Core/Terminal/RowRunBuilder.cs:25`
  `var runs = new List<CellRun>();`——**每脏行每次绘制新建一个 `List`**。
- `TerminalRenderer.cs:266` — `using (var layout = new CanvasTextLayout(ds, text, format, width, CellHeight))`
  在 `DrawGlyphRun` 里，**每行每 run 构造并销毁一个 Win2D/D2D COM 对象**。
  48×30、每行 5 段、30 draw/s 约合每秒 4500 次 COM 创建销毁。
  SP04 实测整屏重绘已经 20.6 ms/帧（预算 33 ms），余量不多。
- → `List<CellRun>` 改为渲染器实例级复用（clear + 重填）；
  文本绘制在无特殊对齐需求时改用预缓存的两种 `CanvasTextFormat`（左对齐/居中）+
  `DrawText(text, rect, color, format)`，避免每 run 一个 layout。

### P2-11 · `fwd::BidirectionalPump` 用 `erase(0, sent)` 排空缓冲（O10）

`native/core/fwd/pump.cpp:76` — 每次部分写都 `memmove` 剩余字节，缓冲逼近 256 KiB
上限时接近 O(n²)。同仓已有正确范式：`native/core/ssh/channel.cpp` 的 `PendingWriteQueue`
（`deque<string>` + `headOffset_`，不搬移）。影响 SFTP-over-tunnel 与 SOCKS5 大流量。

### P2-12 · `EventLoop` 每轮迭代堆分配 + 二次哈希查找（O10）

- `native/core/io/EventLoop.cpp:144-152` — `std::vector<WSAPOLLFD> pollDescriptors`
  是循环体内局部变量，每次唤醒重新构造（`reserve` 只免了增长，免不了这次分配）；
  N 个会话线程各一份。→ 提升为成员，每轮 `clear()` 复用。
- `EventLoop.cpp:176-194` — 派发时对每个就绪 fd 再 `registrations_.find()` 一次，
  而几行前构建 pollfd 时刚读过同一条注册。→ 构建时并行存回调指针。

### P2-13 · Q04 门禁失效，本地化实际未完成（O12 + O13）

- **尺子本身反了**：`scripts/check-hardcoded-text.ps1:30-31` 把「含中文的行」整行豁免
  （`$chinesePattern` 命中即 `continue`），即这个为 Q04 建的检查**恰好检不出它要检的东西**；
  且只扫 `.xaml`，不扫 `.cs`。
- **实测缺口**：非 Debug XAML 中 `Text=/Content=/Header=/PlaceholderText=` 等仍有
  **137 处中文字面量**（如 `Controls/SessionOverlay.xaml:21-35` 的取消/立即重连/停止/重试/编辑主机/关闭、
  各 `Dialogs/*.xaml` 的 `PrimaryButtonText`/`SecondaryButtonText`）；
  C# 中对 `.Text/.Title/.Content/.Header/PrimaryButtonText/…` 赋中文字面量 **37 处**
  （如 `Views/Sync/AccountSyncPage.xaml.cs:86` `Header.Title = "账号与同步";`）。
  80 个非 Debug XAML 中 45 个含 `x:Uid`；`Strings/{zh-cn,en-us}/Resources.resw` 各 796 键，数量已对齐。
- 注意：C# 里的**日志字符串不需要本地化**，门禁只针对用户可见属性赋值，不要误伤。

### P2-14 · 零散项（O14）

- `Platform/AppEntityLookup.cs:67,89,111` 用 `.Result` 同步阻塞仓库异步读（冲突页名称查找）。
  Core 的 `ConfigureAwait(false)` 纪律使其不会死锁，但仍在 UI 线程上阻塞了一次文件 I/O。
- `Core/Terminal/PaneTree.cs:38-43` `Leaves()` 每次调用新建 `List`，
  而 `FindLeaf`/`Contains`/`Split`/`Close`/`SetFocus`/`FindNeighbor` 全部经它 → 改为树内直接查找。
- 十六进制颜色解析三处逐字重复：`Controls/ColorSwatchPicker.xaml.cs:320-342`、
  `Controls/GroupHeader.xaml.cs:60-75`、`ViewModels/AppearanceListViewModel.cs:49-61`
  → 抽一个 `ColorHex.TryParse`。
- 退避表取值逻辑三处重复：`Sessions/ReconnectScheduler.cs:16-20`、
  `Forwarding/TunnelManager.cs:528-529`、`Sync/SyncCoordinator.cs:625-628`。
  **退避表本身按协议各不相同，不要合表**，只抽 `Common/BackoffTable.DelayFor(int[], int)`。
- `Properties/Default.rd.xml` 仍是 `Dynamic="Required All"`（全应用保留元数据），
  而全仓 `JsonConvert`/`JsonSerializer`/`ToObject<>` 用量为 **0**（Core 全走手写
  `JsonTextReader/Writer`，与 `01-DESIGN` R7「禁用反射序列化」一致），
  唯一真反射需求是 XAML `{Binding}`（56 处，对 14 处 `x:Bind`）。
  → 可收窄到 ViewModels/Models 命名空间换取 .NET Native 包体与 ILC 时间，
  **但必须先用 ARM Release 实测包体与冷启动再决定**（§15 冷启动 < 2.5 s），归入 Q09 打包阶段。
- `native/core/ssh/session.cpp:560-572` 每个 Readable 事件多一次 1 字节 `MSG_PEEK`，
  仅用于探测对端优雅关闭；可由通道/pump 的 `Eof`/`Error` 结果替代。收益最小，优先级最低。

---

## 4. 已核查确认、不需要改动的地方

- Core 的异步纪律与取消传递；`Sync/Api/ApiClient.cs` 的重试/退避/单飞刷新；
  `SyncCoordinator`/`TunnelManager`/`ApiClient` 生产路径无 `.Result`/`.Wait()`。
- `Cell` 精确 16 字节且 `static_assert` 钉住偏移；`ScrollbackBuffer`/`CellGrid` 预分配定容。
- Native 的 RAII 与错误路径释放；`diag_counters`（sessions/threads/sockets/screens）
  正是为 Q02 泄漏判定准备的，口径可用。
- `copyDirtyRows` 整屏拷贝（`01-DESIGN` D6 已论证 ~24–34 KB/帧可接受）。
- `channel.cpp` 的 `PendingWriteQueue` 是正确范式——O10 应向它看齐。
- `CellGrid::resize` / `ScrollbackBuffer::resizeCols` 的整表重建：仅旋转/改字号时发生，已在注释中论证。
- `Package.appxmanifest` 能力集最小（internetClient / internetClientServer / privateNetworkClientServer）。
- `src/SshTool.App` 中 301 处 `catch (Exception)`：抽样核查（`Platform/ThemeService.cs`、
  `StatusBarService.cs`、`Terminal/TerminalView.xaml.cs`、`Controls/TerminalWorkspace.xaml.cs`）
  基本都是平台能力探测与指针捕获的防御性守卫，与 C-04 的判断一致，
  属持续清理范畴，本轮不单独立任务。

---

## 5. 与 O 系列任务的对应关系

| 任务 | 覆盖本文条目 |
|---|---|
| O01 | P0-1 |
| O02 | P0-3 |
| O03 | P0-2 |
| O04 | P1-4 |
| O05 | P1-5 |
| O06 | P1-6 |
| O07 | P1-7 |
| O08 | P1-8 |
| O09 | P1-9 |
| O10 | P2-11、P2-12 |
| O11 | P1-10 |
| O12 | P2-13 ①（门禁） |
| O13 | P2-13 ②（清零） |
| O14 | P2-14 |

任务明细（依赖、产出、要点、验收）见 `04-TASKS.md` §11「M9 — 优化与债务清理」。
