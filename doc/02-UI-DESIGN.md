# Lumia SSH UI 设计

> 配套：`01-DESIGN.md`。目标画布：**手机竖屏 360×640 epx**（Lumia 950），横屏 640×360，Continuum 大屏 ≥ 1024 宽。
> 平台可用控件以 15063 为准：`Pivot`、`CommandBar`、`SplitView`、`ListView`（含分组 `CollectionViewSource`）、`ContentDialog`、`MenuFlyout`、`ToggleSwitch`、`ComboBox`、`Slider`、`ProgressRing`、`AutoSuggestBox`。
> **不可用**（≥16299）：NavigationView、SwipeControl、ColorPicker、InfoBar、TeachingTip、Acrylic、Reveal、RefreshContainer（下拉刷新用按钮替代）。

---

## 1. 设计原则

1. **Windows 10 Mobile 原生手感**：页面顶部 Pivot 标题、底部 `CommandBar`（主操作 ≤ 4 个图标 + 「…」二级菜单）、硬件返回键可退出任何弹层。
2. **深色优先**：Lumia 950 是 AMOLED，默认深色主题（接近纯黑背景省电），提供浅色与跟随系统。强调色默认取系统强调色（`UISettings.GetColorValue(UIColorType.Accent)`），可在设置里改为应用蓝。
3. **终端第一**：终端页沉浸（隐藏状态栏，顶部信息条可折叠），任何 UI 元素都不遮挡光标所在行。
4. **单手可达**：高频操作放底部（CommandBar、键条）；危险操作放「…」二级菜单且二次确认。
5. **状态可见**：连接态用状态点 + 文案；同步态在主页底栏常驻图标；错误以 Banner 呈现（带「重试/详情」）而不是瞬逝 Toast。
6. **零魔法数字**：所有颜色、间距、字号、圆角、图标字形来自 `Themes/Tokens*.xaml`。

---

## 2. Design Token

### 2.1 颜色（`Tokens.Dark.xaml` / `Tokens.Light.xaml`，`ThemeDictionaries` 中同名键）

| 键 | 深色 | 浅色 | 用途 |
|---|---|---|---|
| `AppBgBrush` | `#000000` | `#F3F5F9` | 页面底（深色纯黑，AMOLED 省电） |
| `AppSurfaceBrush` | `#12161F` | `#FFFFFF` | 卡片、列表行背景 |
| `AppSurfaceAltBrush` | `#1A2030` | `#EEF1F7` | 分组头、输入框背景 |
| `AppPressedBrush` | `#243048` | `#E2E8F4` | 按压态 |
| `AppBorderBrush` | `#2A3448` | `#D9DFEA` | 分隔线、描边 |
| `AppTextBrush` | `#E8EEFB` | `#1A2233` | 主文字 |
| `AppTextDimBrush` | `#9AA7C0` | `#5D6B85` | 次要文字 |
| `AppTextFaintBrush` | `#6B7891` | `#8A97AE` | 占位、提示 |
| `AppAccentBrush` | 系统强调色（回退 `#4D8DFF`） | 系统强调色（回退 `#2F6FE4`） | 强调、聚焦边框 |
| `AppOnAccentBrush` | `#FFFFFF` | `#FFFFFF` | 强调色上的文字 |
| `AppSuccessBrush` | `#34C77B` | `#16A765` | 已连接、同步成功 |
| `AppWarningBrush` | `#F5A524` | `#C9800C` | 重连中、离线、冲突 |
| `AppDangerBrush` | `#F4605F` | `#DC3F43` | 错误、删除 |
| `AppInfoBrush` | `#3EC6D9` | `#1597A8` | 提示 Banner |
| `KeyBarBgBrush` | `#0B0E14` | `#E6EAF2` | 键条底 |
| `KeyBarKeyBrush` | `#1E2636` | `#FFFFFF` | 键帽 |
| `KeyBarKeyActiveBrush` | 强调色 | 强调色 | 粘滞修饰键已激活 |
| `KeyBarKeyLockedBrush` | `#F5A524` | `#C9800C` | 修饰键已锁定（带下划线） |
| `OverlayScrimBrush` | `#B3000000` | `#99FFFFFF` | 终端蒙层 |

状态点颜色映射：未连接 `AppTextFaintBrush`（空心圆）、连接中/认证中 `AppAccentBrush`（脉动）、已连接 `AppSuccessBrush`、重连中 `AppWarningBrush`（脉动）、错误 `AppDangerBrush`。

### 2.2 尺寸（`Tokens.xaml`，`x:Double` / `Thickness` / `CornerRadius`）

| 键 | 值 | 用途 |
|---|---|---|
| `SpaceXs` / `SpaceSm` / `SpaceMd` / `SpaceLg` / `SpaceXl` | 4 / 8 / 12 / 16 / 24 | 间距 |
| `GapSmLeft` / `GapSmBottom` / `GapLgBottom` / `GapMdTop` / `GapXlTop` | `8,0,0,0` / `0,0,0,8` / `0,0,0,16` / `0,12,0,0` / `0,24,0,0` | 间距：**W10M 15063 既无 `Grid.ColumnSpacing`/`RowSpacing` 也无 `StackPanel.Spacing`（均需 1709）**，一律用子元素 Margin 让出 |
| `BorderThin` / `BorderThinBottom` / `BorderNone` | `1` / `0,0,0,1` / `0` | 发丝描边（Toast 边框、Banner 底线）；`BorderNone` 给隐藏控件去边框 |
| `PadMd` / `PadNone` | `12` / `0` | 均匀内边距（Toast 等）。**`Padding`/`Margin`/`BorderThickness` 必须绑 Thickness token，不能绑 `Space*`（`x:Double`），否则运行时 XamlParseException** |
| `SentinelInputSize` / `SentinelInputOpacity` | `1` / `0.01` | 终端隐藏 IME 哨兵 TextBox（01-DESIGN §7.5：1×1 保持可聚焦，Opacity 0 会被裁掉） |
| `PagePadding` | `12,0,12,0` | 页面左右留白（手机） |
| `PagePaddingWide` | `24,0,24,0` | 宽屏 |
| `RadiusSm` / `RadiusMd` | 4 / 8 | W10M 风格偏直角，圆角克制 |
| `ListRowHeight` | 64 | 主机行 |
| `ListRowCompactHeight` | 48 | 设置行、隧道行 |
| `GroupHeaderHeight` | 36 | 分组头 |
| `StatusDotSize` | 10 | |
| `TouchTargetMin` | 40 | 最小触控尺寸 |
| `KeyBarHeight` | 40 | 键条 |
| `KeyBarKeyMinWidth` | 44 | 键帽 |
| `TerminalInfoBarHeight` | 32 | 终端顶部信息条 |
| `TabStripHeight` | 36 | 宽屏标签栏 |
| `SplitterThickness` | 6 | 分屏分隔条（命中区 16） |
| `SessionsPaneWidth` | 280 | 会话侧栏 |
| `MasterPaneWidth` | 320 | 宽屏主机列表 |
| `DialogMaxWidth` | 340 | 对话框 |
| `FontCaption` / `FontBody` / `FontSubtitle` / `FontTitle` / `FontHeader` | 12 / 15 / 18 / 20 / 28 | 字号（W10M 正文 15） |
| `WideBreakpoint` | 720 | 自适应断点（有效宽度） |

### 2.3 图标（Segoe MDL2 Assets 字形，`Tokens.xaml` 中 `x:String` 键）

| 键 | 字形 | 用途 |
|---|---|---|
| `IconAdd` | `&#xE710;` | 新建 |
| `IconSearch` | `&#xE721;` | 搜索 |
| `IconSync` | `&#xE895;` | 同步 |
| `IconSyncError` | `&#xEA6A;` | 同步错误 |
| `IconCloudOff` | `&#xE8CD;`（回退 `&#xE7BA;`） | 离线 |
| `IconSettings` | `&#xE713;` | 设置 |
| `IconMore` | `&#xE712;` | 更多 |
| `IconConnect` | `&#xE703;` | 连接 |
| `IconDisconnect` | `&#xE8CD;` | 断开 |
| `IconTerminal` | `&#xE756;` | 终端/会话 |
| `IconKey` | `&#xE192;` | 密钥 |
| `IconLock` / `IconUnlock` | `&#xE72E;` / `&#xE785;` | 保险库 |
| `IconEdit` / `IconDelete` | `&#xE70F;` / `&#xE74D;` | 编辑/删除 |
| `IconCopy` / `IconPaste` | `&#xE8C8;` / `&#xE77F;` | 复制/粘贴 |
| `IconFolder` / `IconDocument` | `&#xE8B7;` / `&#xE8A5;` | SFTP |
| `IconUpload` / `IconDownload` | `&#xE898;` / `&#xE896;` | 传输 |
| `IconTunnel` | `&#xE71B;` | 端口转发 |
| `IconSnippet` | `&#xE943;` | 片段 |
| `IconSplitH` / `IconSplitV` | `&#xE7FA;` / `&#xE7FB;`（SP 阶段核对字形） | 分屏 |
| `IconKeyboard` | `&#xE765;` | 键盘 |
| `IconWarning` / `IconError` / `IconInfo` | `&#xE7BA;` / `&#xE783;` / `&#xE946;` | Banner |

### 2.4 文字样式（`Controls.xaml`）

`PageTitleTextStyle`（FontHeader，Light）、`SectionTitleTextStyle`（FontSubtitle，SemiBold）、`BodyTextStyle`、`CaptionTextStyle`（AppTextDimBrush）、`MonoCaptionTextStyle`（Consolas/JetBrains Mono，用于指纹与地址）。

---

## 3. 自适应规则

| 形态 | 判定 | 布局 |
|---|---|---|
| 手机竖屏（Narrow） | 窗口有效宽 < 720 | 单页导航；MainPage Pivot；终端全屏单窗格；会话切换走侧滑 SplitView（Overlay） |
| 手机横屏（Narrow-Landscape） | 宽 < 720 且 宽 > 高 | 同上；终端页信息条自动隐藏，键条保留 |
| Continuum / 大窗口（Wide） | 宽 ≥ 720 | MainPage 变主从：左主机列表（`MasterPaneWidth`），右为会话区（标签栏 + 窗格树）；选择主机即在右侧开标签，不跳页 |

实现：每个页面用 `VisualStateManager` + `AdaptiveTrigger MinWindowWidth={StaticResource WideBreakpoint}`；
Continuum 中同时检查 `UIViewSettings.GetForCurrentView().UserInteractionMode == Mouse` → 显示鼠标友好尺寸（行高降为 `ListRowCompactHeight`）、隐藏键条（可手动打开）。

---

## 4. 导航图与返回行为

```
App 启动
 └─ MainPage（Pivot：主机 | 会话 | 隧道）
     ├─ [主机行 点击] ──────────────→ TerminalPage（Narrow）/ 右侧开标签（Wide）
     ├─ [主机行 长按/右键] → MenuFlyout：连接 / 新标签连接 / 编辑 / 复制为新主机 / SFTP / 删除
     ├─ [+] ───────────────────────→ HostEditPage（新建）
     ├─ [分组头 长按] → 重命名/颜色/删除
     ├─ [同步图标] ─────────────────→ AccountSyncPage
     └─ [… 二级] → 快速连接 | 片段 | 密钥 | 已知主机 | 外观 | 账号与同步 | 设置 | 关于
TerminalPage
 ├─ [会话按钮 / 左缘滑入] → 会话侧栏（SplitView Pane）
 ├─ [片段] → SnippetPickerFlyout
 ├─ [… ] → SFTP | 端口转发（本会话） | 外观 | 断开 | 关闭会话
 └─ SftpPage（从终端或主机菜单进入）
AccountSyncPage（Pivot：状态 | 设备 | 历史）
 ├─ 未登录 → LoginPage（登录/注册切换）
 ├─ 已登录无保险库 → VaultSetupPage
 ├─ 已登录保险库锁定 → VaultUnlockPage
 └─ 冲突 → SyncConflictPage
```

**硬件返回键规则**（`NavigationService` 统一处理，优先级从上到下）：
1. 打开的 `ContentDialog`/Flyout → 关闭它
2. 终端页选择模式 → 退出选择
3. 终端页会话侧栏打开 → 关闭侧栏
4. 终端页软键盘可见 → 收起键盘
5. 编辑页有未保存修改 → 弹「放弃修改？」
6. `Frame.CanGoBack` → 返回（**离开终端页不断开会话**，会话在「会话」Pivot 中可回来）
7. MainPage 且非第一个 Pivot → 切回「主机」Pivot；否则交给系统（退出应用；若有活跃会话先弹确认「有 N 个会话，退出将断开」）

---

## 5. 页面设计

> 线框以 360 epx 宽手机竖屏为基准；`[ ]` 表示按钮，`( )` 表示图标按钮，`▸/▾` 折叠箭头。每页列出：组成、状态、交互、ViewModel 关键属性/命令。

### 5.1 MainPage — 主机 Pivot

```
┌────────────────────────────────────┐
│ LUMIA SSH                           │ ← 应用标题（小号大写，Pivot 标题）
│ 主机    会话    隧道                 │ ← Pivot 头
├────────────────────────────────────┤
│ ┌────────────────────────────────┐ │
│ │ 🔍 搜索主机、地址、用户           │ │ ← AutoSuggestBox（折叠时仅显示在命令栏）
│ └────────────────────────────────┘ │
│ ┌────────────────────────────────┐ │
│ │ ⚡ 快速连接  user@host:port   [→]│ │ ← 快速连接卡（可在设置中隐藏）
│ └────────────────────────────────┘ │
│ ▾ 生产 (3)                    ● ■  │ ← 分组头：名称、数量、分组色块
│ ┌────────────────────────────────┐ │
│ │ ● web-01                     ⋯ │ │ ← 状态点 + 名称
│ │   root@10.0.0.11:22   🔑 tmux  │ │ ← 地址（等宽小字）+ 徽标（密钥/tmux/跳板）
│ └────────────────────────────────┘ │
│ │ ○ db-01                         │ │
│ │   admin@10.0.0.21              │ │
│ ▸ 测试 (5)                          │
│ ▾ 未分组 (2)                        │
│ │ ○ nas                           │ │
├────────────────────────────────────┤
│  (+)     (🔍)    (☁✓)     (…)      │ ← CommandBar：新建 / 搜索 / 同步状态 / 更多
└────────────────────────────────────┘
```

- **排序**：组按 `order`；组内按 `hostSortMode`（名称 / 最近连接）。
- **状态点**：该主机任一会话的最「活跃」状态（Connected > Reconnecting > Connecting > Error > 无）。
- **徽标**：`authType=key` 显示钥匙；`tmuxAutoAttach` 显示 `tmux`；`jumpHostId` 显示 `↪`；有隧道显示隧道图标。
- **同步状态图标**（CommandBar 第三个）：`signed_out` 云+斜杠（灰）、`disabled` 云（灰）、`locked` 锁、`idle/synced` 云+勾、`syncing` 旋转同步图标、`offline` 离线、`conflict` 感叹号（警告色）、`error/auth_error` 错误色。点击进 AccountSyncPage。
- **空状态**（无主机）：居中插画字形 + 「还没有主机」+ [添加主机] + [登录同步已有配置]。
- **搜索无结果**：「没有匹配“xxx”的主机」+ [用它快速连接]（若输入形如 user@host）。
- **快速连接**：输入 `user@host[:port]` → 以临时主机连接（不保存），连接成功后终端顶部 Banner 提示 [保存为主机]。
- 交互：点行 → 连接（已有 Connected 会话时直接切过去，长按菜单才「新会话」）；长按 → MenuFlyout；分组头点击折叠（持久化）。
- VM：`HostListViewModel`：`Groups: ObservableCollection<HostGroupVm>`、`SearchText`、`IsEmpty`、`SyncIcon`、`ConnectCommand`、`NewSessionCommand`、`EditCommand`、`DuplicateCommand`、`DeleteCommand`、`ToggleGroupCommand`、`QuickConnectCommand`。

### 5.2 MainPage — 会话 Pivot

```
│ 主机    会话    隧道                 │
│ ┌────────────────────────────────┐ │
│ │ ● web-01              12:03 起  │ │
│ │   已连接 · tmux:main           ✕│ │ ← 状态文案 + 关闭
│ ├────────────────────────────────┤ │
│ │ ◐ db-01          重连中 5s     ✕│ │
│ │   [立即重连]                    │ │
│ └────────────────────────────────┘ │
│  上次未关闭的会话（2）   [全部恢复] │ ← 冷启动后出现
│  (+新会话)   (全部断开)    (…)     │
```
- 点击会话 → 进入 TerminalPage 并聚焦该会话。空状态：「没有打开的会话，点主机开始」。

### 5.3 MainPage — 隧道 Pivot

```
│ ▾ 默认分组                           │
│ ┌────────────────────────────────┐ │
│ │ [开关] 本地 8080 → db:5432      │ │
│ │   via web-01 · 运行中 · 2 连接  │ │
│ │   ↓1.2KB/s ↑300B/s              │ │
│ ├────────────────────────────────┤ │
│ │ [—] 中转 3306 → mysql:3306     │ │
│ │   仅桌面端运行                  │ │
│ └────────────────────────────────┘ │
│  (+)     (全部停止)     (…)        │
```
- 开关 = 启停（relay 禁用开关）；点行进 TunnelEditPage。local/dynamic 行首次开启时提示回环隔离限制（「本机其他应用无法连接此端口」，可勾「不再提示」）。

### 5.4 HostEditPage（新建/编辑）

```
┌────────────────────────────────────┐
│ 编辑主机                              │
│ 连接   认证   终端   高级             │ ← Pivot
├────────────────────────────────────┤
│ 名称 *            [web-01          ] │
│ 地址 *            [10.0.0.11       ] │
│ 端口              [22    ]           │
│ 用户名 *          [root            ] │
│ 分组              [生产          ▾] │ ← 本机专有（小字标注「仅本机」）
│ 保活间隔(秒)       [30    ]           │
│ 主机指纹          SHA256:ab12…  [清除]│ ← 只读，等宽
│                                     │
│ ⚠ 端口需在 1–65535                   │ ← 字段下方内联错误
├────────────────────────────────────┤
│   (✓保存)   (⚡测试连接)   (✕取消)   │
└────────────────────────────────────┘
```
- **认证 Pivot**：认证方式（RadioButtons：密码 / 私钥 / 应用内 Agent）；
  密码：`PasswordBox` + 「保存在本机」开关（默认开）+ 提示「开启同步密码后会加密上传」；
  私钥：选择已有密钥（ComboBox，显示名称+类型+指纹尾 8 位）+ [导入] [生成]；密钥短语 `PasswordBox` + 「保存」。
- **终端 Pivot**：外观（ComboBox：跟随默认 / 各外观）、终端类型（`xterm-256color`/`xterm`/`vt100`）、退格发送（DEL / Ctrl+H）、环境变量列表（键=值 行，+ 添加）。
- **高级 Pivot**：tmux 自动附着（开关 + 会话名，附说明「断线/被系统挂起后重连回到原现场，推荐开启」）、连接后执行命令（多行文本，每行一条）、跳板主机（ComboBox，排除自己与会成环的主机）、隧道（该主机的隧道列表 + [添加隧道]）。
- 字段标注：☁ 字段无标记；🏠 字段右侧小字「仅本机」。
- 校验：实时校验，保存时汇总；错误跳到对应 Pivot 并聚焦字段。
- **测试连接**：弹进度对话框（连接中→握手→认证→成功/失败原因），首连弹 HostKeyDialog；不创建会话、15 s 超时。
- 返回键有未保存修改 → 确认。
- VM：`HostEditViewModel`：各字段属性、`Errors: Dictionary<string,string>`、`IsDirty`、`SaveCommand`、`TestCommand`、`ImportKeyCommand`、`GenerateKeyCommand`。

### 5.5 TerminalPage（手机竖屏）

```
┌────────────────────────────────────┐
│ ● web-01  root@10.0.0.11   ☰  ⋯   │ ← 信息条 32epx（可点标题收起为 4epx 细条）
├────────────────────────────────────┤
│root@web-01:~# ls                    │
│bin  etc  home  var                  │
│root@web-01:~# █                     │
│                                     │ ← TerminalView（Win2D）
│                                     │
│                                     │
├────────────────────────────────────┤
│ Esc Tab Ctrl Alt ↑ ↓ ← → Home End ▸│ ← KeyBar（横向滚动）40epx
├────────────────────────────────────┤
│        软键盘（弹出时）              │
└────────────────────────────────────┘
```
- 状态栏隐藏（`StatusBar.HideAsync()`），离开页面恢复。
- **信息条**：状态点、名称、`user@host`、[☰ 会话侧栏]、[⋯ 菜单]。菜单：片段、粘贴、复制全部可见、字号 +/−、切换键条、SFTP、本会话隧道、外观、查找（M8）、断开、关闭会话。
- 软键盘不自动弹出；点终端区域弹出。键盘弹出时终端区缩小（行数减少并 resize），光标行保持可见。
- **蒙层（Overlay，半透明 `OverlayScrimBrush`，居中卡片）**：

| 态 | 卡片内容 | 按钮 |
|---|---|---|
| Connecting | ProgressRing + 「正在连接 web-01…」+ 阶段（解析/握手/认证） | [取消] |
| Reconnecting | 「连接已断开，5 秒后第 2 次重连」+ 原因 | [立即重连] [停止] |
| Error | 错误图标 + 中文原因（`Error_<code>`）+ 建议（如 303：主机指纹已改变） | [重试] [编辑主机] [关闭] |
| Closed | 「会话已结束（退出码 0）」 | [重新连接] [关闭] |
| PolicyDisconnected | 「应用在后台时连接被系统断开」+ tmux 未开时建议开启 | [重新连接] |
- 蒙层出现时终端内容仍在下方（置灰），不白屏。
- **会话侧栏**（SplitView Overlay，从左侧滑入，宽 `SessionsPaneWidth`）：会话列表（状态点、名称、状态文案、✕）+ [+ 新会话（打开主机选择）]。
- **选择模式**：长按进入；选区用 `selection` 色；两端圆形拖柄（`TouchTargetMin`）；顶部浮动工具条 [复制] [粘贴] [全选] [分享] [✕]；复制后轻震 + 「已复制」小提示（1.5 s 自动消失的文本条，非系统 Toast）。
- **捏合缩放**：实时显示字号气泡「12」。
- **横屏**：信息条默认收起；键条保留。

### 5.6 KeyBar 控件

- 键 id 表：`esc tab ctrl alt shift up down left right home end pgup pgdn ins del f1..f12 pipe(|) slash(/) backslash(\) minus(-) underscore(_) tilde(~) colon(:) semicolon(;) quote(') dquote(") backtick(`) lt(<) gt(>) lbrace({) rbrace(}) lbracket([) rbracket(]) paste copy snippets hidekb`
- 修饰键三态视觉：普通（`KeyBarKeyBrush`）→ 单次激活（强调色底）→ 锁定（`KeyBarKeyLockedBrush` 底 + 底部 2epx 横线）。
- 方向键长按自动重复（首次 400 ms 后每 60 ms）。
- 最右固定键「⌨」收起/弹出软键盘。
- 触感：每次按键 10 ms 轻震（受 `hapticsEnabled` 控制）。
- 布局在设置页「键盘」中编辑：可用键列表 ↔ 已选键列表，上/下移动。

### 5.7 TerminalPage（Wide / Continuum）

```
┌──────────────┬────────────────────────────────────────────────────┐
│ 主机          │ [web-01 ✕] [db-01 ✕] [+]                 (⧉)(⋯)   │ ← 标签栏
│ 🔍 搜索       ├──────────────────────────────┬─────────────────────┤
│ ▾ 生产        │ root@web-01:~# htop           │ admin@db-01:~$ █    │
│  ● web-01     │ ...                           │                     │
│  ● db-01      │                               │                     │ ← 分屏（聚焦窗格强调色 1epx 边框）
│ ▸ 测试        │                               ├─────────────────────┤
│               │                               │ root@web-01:~# tail │
│ [+ 主机]      │                               │                     │
└──────────────┴──────────────────────────────┴─────────────────────┘
```
- 主机列表可折叠（`SplitView` Inline/CompactInline）。
- 标签右键：重命名、复制会话、向右分屏、向下分屏、关闭其他。窗格右上角悬停出现 [⧉ 分屏] [✕]。
- 分隔条可拖动（6 epx 视觉 / 16 epx 命中，鼠标指针变为 SizeWestEast/SizeNorthSouth）。
- 焦点：点击窗格或 `Ctrl+Alt+方向` 切换；快捷键见 §5.16。
- 键条默认隐藏（鼠标模式），信息条合并进标签。
- 从 Wide 切回 Narrow（断开 Continuum）：窗格树保活，只显示聚焦叶子。

### 5.8 对话框集合（`ContentDialog`，最大宽 `DialogMaxWidth`）

| 对话框 | 内容 | 按钮 |
|---|---|---|
| HostKeyDialog（首次） | 「首次连接 web-01 (10.0.0.11:22)」、密钥类型、`SHA256:…`（等宽，可选中复制）、randomart（等宽 9 行框） | [信任并连接]（主）[取消] |
| HostKeyMismatchDialog | 红色警告：「主机密钥已改变，可能存在中间人攻击」、已记录指纹 vs 当前指纹对比 | [取消]（主）[移除旧记录并重试…]（需再确认输入主机名） |
| CredentialDialog | 「web-01 的密码」、`PasswordBox`、[☐ 保存在本机]、失败时红字「密码错误，还可尝试 2 次」 | [连接] [取消] |
| PassphraseDialog | 「私钥 xxx 的短语」+ 保存开关 | [解锁] [取消] |
| KbdInteractiveDialog | Name/Instruction + 每个 prompt 一个输入框（echo=false 用 PasswordBox） | [提交] [取消] |
| PasteConfirmDialog | 「将粘贴 12 行」+ 前 5 行等宽预览 + [☐ 不再提示] | [粘贴] [取消] |
| SnippetVariableDialog | 片段内容预览（变量已替换高亮）+ 未知变量输入框 | [发送] [取消] |
| ConfirmDeleteDialog | 「删除主机 web-01？相关隧道 2 条、已保存凭据将一并删除」 | [删除]（危险色）[取消] |
| RecoveryKeyDialog | 大号等宽显示 `SPM1-xxxx…-XXXX`（分 3 行）、[复制]、说明「只显示这一次」、[☐ 我已抄写/保存] 勾选后主按钮才可用 | [完成] |
| ExitWithSessionsDialog | 「有 3 个会话正在连接，退出将全部断开」 | [退出] [取消] |

### 5.9 SnippetsPage / SnippetEditPage

```
│ 片段                                  │
│ 🔍 搜索                               │
│ ▾ 运维                                │
│  重启 nginx                       ▶   │ ← ▶ 在当前会话发送（无会话则禁用）
│  sudo systemctl restart nginx         │
│ ▾ 未分组                              │
│  (+)   (…)                            │
```
编辑页：名称、分组名（可输入新分组）、内容（多行等宽）、[☐ 发送后回车]、变量说明「可用 ${host} ${user} ${port} ${name}」。
终端里「片段」打开 `SnippetPickerFlyout`（搜索 + 列表，点击发送；含未知变量弹 SnippetVariableDialog）。

### 5.10 KeysPage / KeyDetailPage / KeyGenerateDialog

```
│ 密钥                                   │
│ ┌──────────────────────────────────┐ │
│ │ 🔑 lumia-ed25519                  │ │
│ │   ED25519 · SHA256:ab12…9f3c      │ │
│ │   被 3 台主机使用                  │ │
│ └──────────────────────────────────┘ │
│  (+生成)  (导入)  (…)                  │
```
- 导入：[从文件…]（FileOpenPicker，所有文件）/ [粘贴]（多行文本框）→ 解析（加密则要短语）→ 显示类型/指纹确认 → 命名保存。
- 生成：类型（ED25519 推荐 / RSA 3072 / RSA 4096）、名称、注释、[☐ 设置短语]（首版生成未加密私钥时此项隐藏）→ 生成中进度 → 完成跳详情。
- 详情：名称（可改）、类型、位数、格式、指纹、公钥（等宽多行，[复制] [分享]）、使用它的主机列表、[导出私钥]（需确认 + 警告）、[删除]（被引用时禁止并列出主机）。

### 5.11 KnownHostsPage

列表：`host:port`、密钥类型、指纹尾部、添加时间；点击看完整指纹与 randomart；长按删除（确认）。顶部搜索。

### 5.12 AppearanceListPage / AppearanceEditPage

```
AppearanceEditPage（竖屏上下分栏）
┌────────────────────────────────────┐
│ 预览                                  │
│ ┌────────────────────────────────┐ │
│ │user@host:~$ ls --color          │ │ ← 固定样例文本（16 色、粗体、下划线、反色、光标）
│ │dir  file.txt  run.sh            │ │
│ └────────────────────────────────┘ │
│ 字体 字号 颜色 光标                    │ ← Pivot
│ 字号        ──●────── 12             │
│ 行高        ────●──── 1.2            │
│ 粗体用亮色   [开]                     │
│ 内边距      ──●────── 4              │
├────────────────────────────────────┤
│  (✓保存)  (↺还原)  (…复制为新主题)     │
```
- **颜色 Pivot**：前景/背景/光标/选区 + ANSI 0–15（两行 8 列色块网格），点色块弹 `ColorSwatchPicker`（预设色板 + `#RRGGBB` 输入 + H/S/V 三个滑块 + 新旧对比）。
- 预览 100 ms 内生效；未保存返回 → 确认放弃并完全还原。
- 内置主题只读，编辑时自动「复制为新主题」。
- 列表页：主题卡片（名称 + 8 色条 + 「默认」徽标）、[设为默认]、[导入]（.itermcolors / Windows Terminal JSON）。

### 5.13 AccountSyncPage

**未登录 → LoginPage**
```
│ 账号与同步                             │
│ ┌────────────────────────────────┐ │
│ │ ⚠ 当前同步服务器使用未加密连接，  │ │ ← Banner（警告色）
│ │   登录密码可能被同一网络窃听      │ │
│ └────────────────────────────────┘ │
│ 登录    注册                          │ ← Pivot 或分段按钮
│ 邮箱        [                     ]  │
│ 密码        [                     ]  │
│ (注册) 确认密码 / 邀请码（可选）        │
│ 设备名称    [Lumia 950            ]  │
│             [ 登录 ]                  │
│ 与桌面端 SSH Tool、鸿蒙端使用同一账号，  │
│ 主机与隧道将自动在设备间同步。          │
```
- 注册密码至少 10 位（服务端规则），实时提示。
- 错误码映射：`AUTH_INVALID_CREDENTIALS`「邮箱或密码错误」、`DEVICE_QUOTA_EXCEEDED`「设备数已达上限，请在其他设备上撤销旧设备」、`RATE_LIMITED`「尝试过于频繁，请 N 秒后再试」等（resw 键 `Api_<CODE>`）。

**已登录 → 状态 Pivot**
```
│ 状态    设备    历史                  │
│ ┌────────────────────────────────┐ │
│ │ ☁✓ 已同步                         │ │ ← 相位图标 + 文案（大）
│ │ user@example.com · Lumia 950     │ │
│ │ 版本 r42 · 密钥 v2                 │ │
│ │ 上次同步 2 分钟前                  │ │
│ │ （离线时）下次重试 30 秒后          │ │
│ │ [立即同步]                        │ │
│ └────────────────────────────────┘ │
│ 同步设置                               │
│  启用同步            [开]              │
│  自动同步            [开]              │
│  同步密码与短语       [关]  ⓘ           │ ← 从开→关会进入「安全清理」流程
│  同步私钥            [关]  ⓘ           │
│ 保险库                                 │
│  [锁定保险库]  [修改同步密码]            │
│ 账号                                   │
│  [修改登录密码] [退出登录] [退出所有设备] │
│ 危险操作                               │
│  [删除云端保险库…] [注销账号…]           │
```
- 相位文案：`signed_out` 未登录｜`disabled` 同步未开启｜`locked` 保险库已锁定（[解锁]）｜`idle` 等待同步｜`syncing` 正在同步…｜`synced` 已同步｜`offline` 离线，稍后重试｜`conflict` 需要处理冲突（[处理]）｜`error` 同步失败：message（[重试]）｜`auth_error` 登录已失效（[重新登录]）。
- `vault = missing`（账号从未建库）→ 状态卡显示 [创建同步保险库] → VaultSetupPage。

**VaultSetupPage**：说明「同步密码用于加密你的配置，服务器无法解密；与登录密码可以不同」、同步密码 ×2（强度提示，至少 8 位）、[创建]（解锁后的保险库密钥加密保存在本机，重启无需再次输入，故不提供「记住同步密码」） → 进度「正在生成密钥（约数秒）」→ RecoveryKeyDialog（必须勾选已保存）→ 自动首次同步。

**VaultUnlockPage**：分段「同步密码 / 恢复密钥」，输入框（恢复密钥等宽、自动大写校验段格式 `SPM1-...-XXXXXXXXXXXX`）、[解锁] → 进度 → 成功回状态页并同步。失败：「同步密码不正确」「恢复密钥格式无效/校验失败」。

**设备 Pivot**：列表（名称、平台、版本、最近活跃、「本机」徽标）；长按：重命名 / 撤销（本机禁用撤销，提示用「退出登录」）。

**历史 Pivot**：版本列表（r号、时间、来源设备名/平台、keyVersion）；长按：「恢复到此版本」（确认：「将以此版本创建新的云端版本并覆盖本机配置」）；底部 [清空历史]（确认）。

**安全清理流程（关闭敏感同步 / 修改同步密码）**：对话框页：说明「将生成新的加密密钥与恢复密钥、清除云端历史版本，旧恢复密钥立即失效」→ 账号登录密码 + 新同步密码 ×2 → [执行] → RecoveryKeyDialog。失败时开关回滚。

**注销账号**：输入当前密码 + 输入 `DELETE` 才可点击。

### 5.14 SyncConflictPage

```
│ 同步冲突                               │
│ ┌────────────────────────────────┐ │
│ │ 本机和云端都修改了配置              │ │ ← 按 reason 显示：
│ │                                   │ │   initial-import：首次同步，云端已有配置
│ │ 云端：12 台主机 · 5 条隧道 · 2 分组 │ │   remote-deletion：云端删除了以下项目
│ │       含已同步密码                 │ │   merge-conflict：以下字段两边都改了
│ │ 云端更新于 10:32（r42）            │ │
│ │ 本机更新于 10:30                   │ │
│ └────────────────────────────────┘ │
│ 涉及项目                               │
│  主机 web-01 · profile.port            │
│  主机 db-01 · secrets.password  🔒 有变更 │ ← 敏感字段不显示值
│  隧道 mysql · *（删除 vs 修改）          │
├────────────────────────────────────┤
│ [使用云端配置]    [保留本机配置]         │
```
- initial-import 时按钮文案：[使用云端配置（推荐）] [用本机覆盖云端]。
- 实体名称从本机或云端文档查出显示，查不到显示 id。

### 5.15 SettingsPage（Pivot：通用 | 终端 | 键盘 | 连接 | 关于）

| Pivot | 项 |
|---|---|
| 通用 | 主题（跟随系统/深色/浅色）、使用系统强调色、主机排序、显示快速连接卡、触感反馈、语言（跟随系统/简体中文/English，重启生效） |
| 终端 | 默认外观（→外观列表）、默认字号、回滚行数（1000–50000）、Alt-screen 滚动（方向键/滚轮）、多行粘贴确认、光标闪烁跟随外观 |
| 键盘 | 显示键条、键条布局编辑、退格默认发送、快捷键表（Continuum，列出动作 ↔ 组合键，点击录制新组合） |
| 连接 | 屏幕常亮（从不/会话中/总是）、后台保持连接、后台 N 分钟后断开（关/5/15/30/60）、自动重连次数（0–10）、连接超时（5–60 s）、同步轮询间隔（关/1/5/15 分钟） |
| 关于 | 版本、开源许可（→LicensesPage）、日志（级别、[导出日志]、[清空日志]）、[诊断信息]（系统版本、内存上限、SDK 能力探测结果） |

### 5.16 快捷键（Continuum，可自定义，默认值）

| 动作 | 组合 |
|---|---|
| 新标签（打开主机选择） | Ctrl+Shift+T |
| 关闭当前窗格/标签 | Ctrl+Shift+W |
| 下一个/上一个标签 | Ctrl+Tab / Ctrl+Shift+Tab |
| 向右分屏 / 向下分屏 | Ctrl+Shift+D / Ctrl+Shift+E |
| 切换聚焦窗格 | Ctrl+Alt+←↑→↓ |
| 复制 / 粘贴 | Ctrl+Shift+C / Ctrl+Shift+V |
| 字号 + / − / 重置 | Ctrl+= / Ctrl+- / Ctrl+0 |
| 查找 | Ctrl+Shift+F |
> 快捷键优先于终端按键；未命中的组合原样送终端（所以默认都带 Shift，避免抢 Ctrl+C 等）。

### 5.17 SftpPage

```
│ web-01 · SFTP                          │
│ / home / root / logs          (↑)      │ ← 面包屑，可点
│ ┌────────────────────────────────┐ │
│ │ 📁 archive          —   9月15    │ │
│ │ 📄 app.log      12.3MB   10:02   │ │
│ │ 🔗 current → app.log             │ │
│ └────────────────────────────────┘ │
│ 传输（2）  ▸                            │ ← 可展开的传输队列条
│  (上传)  (新建文件夹)  (刷新)  (…)       │
```
- 行长按：下载、重命名、权限（rwx 九宫格 + 八进制）、删除、复制路径。
- 传输队列展开：每项名称、方向、进度条、速率、[取消]/[重试]。
- 排序（…菜单）：名称/大小/时间，显示隐藏文件开关。

### 5.18 TunnelEditPage

字段：名称 *、类型（本地/远程/动态/中转）、所属主机 *（ComboBox）、分组（隧道分组，☁）、监听地址（默认 local/dynamic `127.0.0.1`，remote `127.0.0.1`）、监听端口 *、目标地址（dynamic 隐藏）、目标端口（dynamic 隐藏）、目标服务器（仅 relay）、意外断开自动重连、启用、应用启动时自动开启（🏠）。
每种类型下方一句话图示：`本机 127.0.0.1:8080 → web-01 → db:5432`。

### 5.19 LicensesPage / DebugPage

- Licenses：列出 OpenSSL、libssh2、libvterm、argon2、zlib、Win2D、Newtonsoft.Json、JetBrains Mono 名称、版本、许可证全文（可展开）。
- DebugPage（仅 Debug 构建入口，M1 演示用）：主机/端口/用户/密码输入 + [连接并执行 `uname -a`] + 原始输出文本框 + 事件日志。

---

## 6. 通用组件（`Controls/`）

| 组件 | 说明 |
|---|---|
| `StatusDot` | 依赖属性 `State`，内部映射颜色与脉动动画（Storyboard 透明度 0.4↔1，1.2 s） |
| `Banner` | Severity（Info/Warning/Error/Success）、Title、Message、ActionText + Command、可关闭；替代 InfoBar |
| `EmptyState` | 字形图标 + 标题 + 说明 + 最多两个按钮 |
| `SectionHeader` | 分组标题 + 右侧可选操作按钮 |
| `LoadingOverlay` | 半透明遮罩 + ProgressRing + 文案，阻止交互 |
| `ColorSwatchPicker` | Flyout：预设 24 色 + Hex 输入 + HSV 滑块 + 新旧对比 |
| `RandomArtView` | 等宽 TextBlock 渲染 randomart |
| `MonoText` | 等宽可选中文本（指纹、公钥） |
| `KeyBar` | §5.6 |
| `TerminalView` | Win2D 终端（§01-DESIGN 7） |
| `TransientToast` | 页面内底部 1.5 s 提示条（非系统通知） |

---

## 7. 文案与本地化

- 所有用户可见文案放 `Strings/zh-CN/Resources.resw` 与 `Strings/en-US/Resources.resw`；XAML 用 `x:Uid`，C# 用 `ResourceLoader.GetForCurrentView().GetString("Key")`。
- 键命名：`<页面或组件>_<元素>_<属性>`，如 `HostEdit_NameBox.Header`；错误码 `Error_<数值>`；API 错误 `Api_<CODE>`；同步相位 `SyncPhase_<phase>`。
- 语气：简短、陈述事实 + 给出路（「连接超时。检查地址和网络后重试」），不用「未知错误」。
- 数字与时间：相对时间（「2 分钟前」）在 1 小时内使用，否则 `MM-dd HH:mm`。

---

## 8. 动效与触感

| 场景 | 动效 | 触感 |
|---|---|---|
| 页面导航 | 系统默认（`EntranceThemeTransition`） | — |
| 状态点连接中/重连中 | 透明度脉动 | — |
| 连接成功 | 蒙层淡出 150 ms | 轻震 15 ms |
| 断开 | 蒙层淡入 150 ms | 震两下 15 ms |
| 键条按键 | 按压背景色 | 10 ms（可关） |
| 复制成功 | TransientToast | 15 ms |
| 粘滞键切换 | 背景色切换 | 10 ms |
| 同步中图标 | 旋转 1 s/圈 | — |
