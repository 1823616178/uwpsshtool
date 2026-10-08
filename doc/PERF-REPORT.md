# Q01 性能基准报告（doc/PERF-REPORT.md）

> 目标：验证 `doc/01-DESIGN.md §15` 的性能与资源预算，逐项给出实测数据、达标结论或差距原因。
> 测试工具：应用内调试页「Q01 性能基准」（`src/SshTool.App/Views/Debug/PerfPage.xaml(.cs)`），
> 所有场景**本地化**（预生成字节流直喂 native vterm，不经 SSH、不依赖真服务器）。
> 本报告先立骨架；📱 数据待真机（Lumia 950，Release/ARM，.NET Native）采集后回填。

- 测试机：待填（Lumia 950 / 固件版本）
- 构建：待填（Release / ARM / .NET Native，包版本用调试页头部 BuildTag）
- 采集日期：待填
- 原始数据：每场景落盘 `LocalState\spike-reports\perf-q01*.txt`（设备门户自取）

## 1. §15 指标表

| 指标 | 目标 | 实测 | 结论 |
|---|---|---|---|
| 冷启动到主机列表可交互 | < 2.5 s（Release/.NET Native） | 待真机 | 📱 |
| 点击主机到出现提示符（局域网） | < 2 s（不含认证交互） | 待真机（需真服务器；Q01 不含） | 📱 |
| `cat` 1 MB 文本 | 全程 ≥ 30 fps，无卡死；完成后 CPU 回落 | 场景①（base64 1MB）待真机 | 📱 |
| 静止终端 | 无帧回调订阅（CPU ≈ 0） | 场景⓪（draw/s=0 且 FrameScheduler.IsRunning=False）待真机 | 📱 |
| 按键回显 | < 50 ms + RTT | 待真机（SP05/T09 成品路径） | 📱 |
| 100 台主机列表滚动 | 无明显卡顿（虚拟化 ListView） | 场景④待真机 | 📱 |
| 内存：单会话 | < 120 MB | 场景①②③运行中读数待真机 | 📱 |
| 内存：4 会话 + 各 5000 行回滚 | < 250 MB | 场景⑤（Δ内存 + 回滚行数）待真机 | 📱 |
| Argon2id 解锁保险库 | 有进度提示；< 8 s | 待真机（S05/V 页自检计时） | 📱 |

> 注：「点击主机到出现提示符」依赖真服务器，不属于本页自动场景；PerfPage 只覆盖
> 本地可测项（⓪①②③④⑤）。真机采集时按 §2 指南逐场景执行，报告文件整份取回后回填 §1。

## 2. 场景操作指南（真机）

前置：**Release/ARM 包**（.NET Native 才算数，见 doc/ENV.md），侧载后从
主页 ⋯ 溢出菜单 →「开发工具」（DevToolsPage，DEBUG_PAGES 门控；opt/full-pass 起 Release 默认关，
采数包需 `-p:EnableDebugPages=true`）→「Q01 性能基准」进入。叠加读数每 500 ms 刷新：
`tick x/s | 实绘 x/s | 帧 x ms | 喂 x ms/帧 | 内存 xMB`。

口径（与报告文件头部一致）：

- `tick/s`：CompositionTarget.Rendering 回调频率（页面 FPS 叠加自身订阅，恒有底噪）；
- `draw/s`：终端 Canvas 实绘次数（SP04 双口径的第二口径；静止时应为 0）；
- `帧 avg/max`：相邻 Rendering 回调间隔（≥1 s 视为中断，不进样本）；
- `喂`：每块字节流灌入耗时（含 native vterm 解析，与渲染并行）；
- 内存：`MemoryManager.AppMemoryUsage`（ApiInformation 守卫，缺失时报「不可用」）。

| 场景 | 按钮 | 等价命令/负载 | 看什么 |
|---|---|---|---|
| ⓪ 静止 | `⓪ 静止读数` | 无（触发一次修订后静置 2.5 s） | `draw=0.0/s`、`FrameScheduler.IsRunning=False` |
| ① base64 | `① base64 1MB` | `head -c 1048576 /dev/urandom \| base64`（预生成 ~1.4 MB，4 KB/帧喂） | 全程 `draw ≥ 30/s`、`帧 max` 无异常长尾；结束后内存回落 |
| ② yes | `② yes 20万行` | `yes \| head -n 200000`（预生成 200 000 行） | 同上，整屏滚动最坏负载 |
| ③ vim | `③ vim 600页` | vim 大文件翻页（alt screen 整页重绘 ×600 页，不进回滚） | 同上；alt 屏重绘成本 |
| ④ 列表 | `④ 100主机滚动`（先 `生成100台`） | 主机列表 8 s 往返滚动（真实 HostRow + 分组虚拟化列表） | `tick` 与 `帧 max`，肉眼无卡顿 |
| ⑤ 回滚 | `⑤ 4×5000回滚` | 4 个会话各推 5000+ 行进回滚环形缓冲（不渲染，纯内存） | `Δ内存`（§15 < 250 MB）、每屏回滚=5000 |

操作：

1. 逐场景：点对应按钮，完成后报告自动展开在页面上；`复制报告` 进剪贴板。
2. 一键全跑：`跑全场景并导出` 按 ⓪→①→②→③→⑤→④ 顺序执行（全程约 40 s，中途不要切走页面），
   结束后整份报告落盘。
3. 取回：设备门户（phone-portal）下载 `LocalState\spike-reports\perf-*.txt`；
   同一份内容也写入了 app.log 与剪贴板。
4. ④ 需要 100 台主机：先点 `生成100台`（同主页面调试入口「生成 100 台测试主机」，
   写入真实主机库的「测试主机」分组；采数后手动删除该分组）。

## 3. 实测数据（待真机回填）

### 3.1 全场景一览（perf-q01-*.txt 的行摘）

```
（待真机：⓪①②③⑤④ 各行，含 tick/s、draw/s、帧avg/max、喂、回滚行数、内存始→末）
```

### 4. 达标判定与差距分析（待真机）

```
（待真机：逐行对比 §15 目标；不达标项给出剖析与原因；优化提交记录在此追加）
```

- 已知口径限制（真机数据解读时注意）：
  - `tick/s` 含本页 FPS 叠加自身的 Rendering 订阅底噪；判定 30 fps 目标看 `draw/s` 与 `帧 avg/max`；
  - 场景①②③的喂入节奏为「每 16 ms 一块」，模拟 `cat` 的持续流；喂入耗时（`喂`）与渲染并行，
    两者之和才是每帧真实负担；
  - 场景⑤只量 native 回滚 + 网格的内存（不渲染）；「4 会话」的会话管理开销另由 Q02 长稳覆盖。

## 5. Q02 稳定性与泄漏（脚本口径与操作指南）

PerfPage 新增 Q02 区（目标主机/端口/用户名/密码输入框，默认取 `DebugSshDefaults`，
需真机局域网可达一台 SSH 服务器；密码不落报告）：

| 脚本 | 按钮 | 做什么 | 看什么 |
|---|---|---|---|
| 连接/断开 ×100 | `Q02 连接/断开×100` | 完整走 连接→主机密钥自动接受→密码认证→立即 Close/Dispose 共 100 轮，每 10 轮采一行（内存 + native 计数），结束后 GC 静置 5 s | 「Δ=…%（验收 ≤ +10%）」；`sessions/threads/sockets/screens` 应全部回到基线，回不去的项即泄漏点 |
| 长稳 | `Q02 长稳开始/停止` | 连接 + 认证 + 开 shell 并挂到页面终端实时渲染（覆盖保活 + 渲染路径），每 60 s 采样，最长 4 h；`Q02 停止` 可随时中止（数据仍落盘） | 内存曲线是否单调上涨；会话掉线（Disconnected 非崩溃但记录）；4 h 无崩溃 |
| 挂起/恢复 | （自动） | 页面订阅应用 Suspending/Resuming，自动计数并记录每次恢复时的内存与 native 计数 | 目标 20 次循环后会话存活、内存回落；计数随两份报告落盘 |

native 资源计数（Q02 新增 `NativeInfo.DiagCounters()`，实现在
`native/core/diag_counters.{h,cpp}`，Debug/Release 都编译——真机 Release ARM 采数可读）：

- `sessions`：ssh::SshSession 存活数（构造/析构）；
- `threads`：io::SessionThread 存活数；
- `sockets`：EventLoop 注册 socket 数（析构时扣除残留，保证可回归）；
- `screens`：Bridge TerminalScreen 存活数（终端网格对象）。

报告落盘：`perf-q02-loop-*.txt` 与 `perf-q02-long-*.txt`（设备门户自取）。

### 5.1 实测数据（待真机回填）

```
（待真机：连接/断开×100 的基线/每 10 轮采样/结束/Δ；长稳逐分钟曲线摘要；
  挂起/恢复计数。回填后按「100 次循环内存 ≤ 基线 +10%、4 h 无崩溃」判定，
  发现泄漏在此追加剖析与修复提交记录。）
```

## 6. 变更记录

- 2026-09-20：建立报告骨架与 PerfPage 脚手架（场景全部本地化，Bridge.TerminalScreen
  新增 FeedBytes/ResizeGrid 公开入口供字节流直喂）；📱 数据待真机采集。
- 2026-09-21：Q01 代码任务完成核验——PerfPage 六场景/FPS 叠加/内存读数/报告落盘
  均已实现并随 verify 全绿构建；页面引用的 token 与 API（FeedBytes/ResizeGrid/
  ScrollbackCount/FrameSchedulerCore.IsRunning/HostsPivot.Attach/DebugReport.*）
  逐一核对存在，入口经 DevToolsPage（DEBUG_PAGES 默认开，Release 可用）。📱
  数据采集指南见 §2，真机回填后按 §4 判定并追加针对性优化。
