# Lumia SSH UI 精修任务书（交给外部 AI 执行）

> 编写：2026-09-28。执行者：任意代码助手（例如 Gemini），**不需要**读过之前的对话。
> 目标：在不改业务逻辑的前提下，把 UI 从「能用、组件拼得齐」提升到「统一、精致、像一个产品」。
> 本文自包含：背景、硬约束、现状诊断、设计规格、逐条任务、验收方法、进度日志都在这里。

---

## 0. 给执行者的开场提示词（复制这一段开始每次会话）

```
你在仓库 G:\code\uwpsshtool 工作（Lumia SSH：Windows 10 Mobile 上的 UWP SSH 客户端，C# 7.3 + XAML + C++/CX）。
先完整阅读：CLAUDE.md、doc/07-UI-POLISH-TASKS.md（本任务书）、doc/02-UI-DESIGN.md §2（Design Token）。
然后按本任务书第 5 节「工作流」执行：从第 6 节里选出第一个未勾选且依赖已满足的任务，只做这一个，
完成后按验收清单自查、跑门禁、勾选、写进度日志、提交。不要跳着做，不要一次做多个任务。
任何与第 2 节硬约束冲突的做法都不允许，哪怕看起来更好看。
```

---

## 1. 背景（执行者必须知道的事实）

- **目标设备**：Lumia 950 / 950 XL，Windows 10 Mobile，**最低系统 10.0.15063**，ARM32；也跑在 Continuum（接显示器 + 键鼠）和 x64 桌面（开发调试）。
- **技术栈**：UWP，C# 7.3（不能用 C# 8+ 语法），XAML，Win2D 画终端，Native 层 C++/CX（UI 任务不应碰 Native）。
- **构建是两段式**（见 CLAUDE.md）：Native 由 VS2017 v141 构建出 `SshTool.Native.winmd`，C# 由 VS2026 构建。UI 任务只改 C#/XAML，一般只需第二段；如果你拉取的代码里 Native 有变更，先跑第一段。
- **设计体系已经存在**，不是从零开始：
  - `src/SshTool.App/Themes/Tokens.xaml`：间距、尺寸、字号、圆角、图标字形（MDL2 码位）。
  - `Themes/Tokens.Dark.xaml` / `Tokens.Light.xaml`：颜色（深色为主，AMOLED 纯黑背景）。
  - `Themes/Controls.xaml`：文本样式与少量按钮样式。
  - 自研组件（`src/SshTool.App/Controls/`）：`AppPageHeader`、`AppListRow`、`SurfaceCard`、`FormSection`、`SegmentTabs`、`BottomActionBar`、`StatusPill`、`StatusDot`、`Banner`、`EmptyState`、`LoadingOverlay`、`TransientToast`、`SectionHeader`、`HostRow`、`TunnelRow`、`KeyBar`、`TabStrip` 等。
  - 组件与 token 的演示页：`Views/Debug/TokenGalleryPage.xaml`（DEBUG 构建主页「更多 → 开发工具 → Token 画廊」）。
- **历史设计文档**：`doc/02-UI-DESIGN.md`（原始 UI 规格）、`doc/05-CODE-AUDIT-UI-REDESIGN.md`（V2 重构方案，§5 视觉层级、§6 页面蓝图）。本任务书是在它们之上的「精修」，冲突时以本任务书为准。

---

## 2. 硬约束（违反任何一条，任务不算完成）

1. **W10M 15063 兼容**：不得使用 15063 之后才有的 XAML 控件与特性。明确禁止：`NavigationView`、`AcrylicBrush`、`RevealBrush`、`InfoBar`、`TeachingTip`、`ThemeShadow`/`DropShadow`、`Grid.ColumnSpacing`/`RowSpacing`、`StackPanel.Spacing`、`CornerRadius` 写在非 `Border`/`Grid`/模板内部的控件上（Control.CornerRadius 是 1809 才有）、`TextBox.BeforeTextChanging`、`CommandBar.OverflowButtonVisibility`、`XamlUICommand`、`x:Bind` 函数绑定。拿不准的 API 先查它的「最低版本」，高于 15063 必须 `ApiInformation` 守卫并有降级。
2. **禁止魔法数字与硬编码颜色**：XAML 里不写 `FontSize="14"`、`Margin="8,0,0,0"`、`#RRGGBB`。一律引用 `Tokens.xaml` / `Tokens.Dark.xaml` / `Tokens.Light.xaml` 的资源；缺什么 token 就**先加 token**（深浅两套颜色都要加），再引用。门禁 `scripts/check-magic-numbers.ps1` 会检查。
3. **所有可见文案走资源**：XAML 用 `x:Uid`，C# 用 `Localized.Get("键", "中文兜底")` / `Localized.Format(...)`。`src/SshTool.App/Strings/zh-cn/Resources.resw` 与 `en-us/Resources.resw` **两份键集合必须完全一致**（单测 `ResourceParityTests` 会查）。门禁 `scripts/check-hardcoded-text.ps1` 会查中文字面量。**注意**：C# 里 `ResourceLoader.GetString` 读带属性的键要用斜杠（`"Foo/Text"`），写成 `"Foo.Text"` 会返回空串。
4. **终端页焦点纪律**：终端页（`Views/TerminalPage.xaml`、`Controls/KeyBar`、`Controls/TerminalWorkspace`、`Controls/TabStrip`）上所有可点控件必须 `AllowFocusOnInteraction="False"`，否则点一下就把焦点从输入哨兵抢走、软键盘收起。**不要**给终端的隐藏输入框 `Sentinel`（`Terminal/TerminalView.xaml`）套任何全局 TextBox 样式——做全局 TextBox 样式时必须让它显式 `Style="{x:Null}"` 或确认隐式样式不改变它的尺寸、边框与模板。
5. **不改业务逻辑与数据格式**：不改 ViewModel 的对外契约（属性名、命令名），不改 `x:Name`（代码隐藏在用），不动 `src/SshTool.Core/Sync/` 与同步相关的任何东西，不动 Native。纯视觉改造只允许改 XAML、Themes、样式相关的代码隐藏。
6. **触控目标 ≥ 40×40 epx**（token `TouchTargetMin`）；关键按钮建议 44。
7. **无障碍**：图标按钮必须有本地化的 `AutomationProperties.Name`（经 `x:Uid` 的 `.AutomationProperties.Name` 键）；状态不能只靠颜色表达（同时有文字或形状）。
8. **深浅两套主题都要好看**：每个改动都要在深色与浅色下检查（设置 → 通用 → 主题）。
9. **日志脱敏**：不要在任何新代码里记录密码、私钥、主机地址、用户名、终端内容。
10. **每个任务一个提交**，提交信息格式：`style(Gxx): 简述`；不要把多个任务揉进一个提交。

---

## 3. 现状诊断（为什么「还差点意思」）

按影响从大到小：

1. **平台默认控件没有统一皮肤**。全仓非调试页面里约有 57 个 `TextBox`、27 个 `ToggleSwitch`、22 个 `PasswordBox`、22 个 `ComboBox`、17 个 `ContentDialog`、15 个 `CheckBox`、11 个 `Slider`、7 个 `CommandBar`、6 个 `RadioButton`、5 个 `AutoSuggestBox`、4 个 `Pivot`，而 `Themes/Controls.xaml` 只定义了文本样式和几个按钮样式。这些控件用的是 W10M 原生外观（方角白边输入框、系统蓝、默认对话框），和自研组件（深色表面、圆角、细边框）放在一起，就是「拼起来的」感觉。**这是第一优先级。**
2. **表单页结构不一致**：`HostEditPage`、`TunnelEditPage` 用了 `FormSection`；`SnippetEditPage`、`AppearanceEditPage`、`GroupManagePage`、`KeyBarLayoutEditorPage`、`ShortcutEditorPage`、登录/建库/解锁等页面还是 `StackPanel` 里平铺控件，缺分组、说明文字层级与间距节奏。
3. **底部操作区两套写法**：一部分页面用自研 `BottomActionBar`，另一部分用 `Page.BottomAppBar` + `CommandBar`（SFTP、外观列表、分组管理、片段编辑等），视觉与交互不统一。
4. **设置页是一个超长 Pivot 表单**（`Views/SettingsPage.xaml`，5 个 Pivot 项），`05` 文档 §6.4 计划的「设置首页 = 分组行 + 当前值摘要，二级页编辑」没有做。
5. **缺少动效与反馈层次**：页面进入、列表出现、展开折叠都是硬切；只有少数组件有按压态。
6. **图标语义有限**：`Tokens.xaml` 里只有约 40 个图标 token；SFTP 文件没有按类型区分的图标，主机没有可识别的头像/色标。
7. **浅色主题与英文文案从未被系统检查过**：真机验收待办里所有观感项都还没做。

---

## 4. 设计规格（所有任务共同遵守）

关键词：**终端优先、OLED 友好、层级清晰、克制**。不追求花哨，追求一致与精致。

### 4.1 表面与颜色（沿用现有 token，只允许补充，不允许推翻）

| 角色 | Token | 用途 |
|---|---|---|
| Canvas | `AppBgBrush` | 页面与终端背景（深色纯黑） |
| Surface 1 | `AppSurfaceBrush` | 列表行、卡片、页头、底栏 |
| Surface 2 | `AppSurfaceAltBrush` | 输入框底、选中态、浮层（对话框、菜单） |
| Pressed | `AppPressedBrush` | 按压反馈 |
| Stroke | `AppBorderBrush` | 分隔线与输入框边框（默认低对比） |
| Accent | `AppAccentBrush` | 焦点边框、主操作、选中指示；**一个页面最多一个强强调元素** |
| Semantic | `AppSuccess/Warning/Danger/InfoBrush` | 只用于状态点、图标、细边、短标签，不做大面积底色 |
| 文本 | `AppTextBrush` / `AppTextDimBrush` / `AppTextFaintBrush` | 主文本 / 次要 / 占位与禁用 |

### 4.2 字体层级（`Tokens.xaml` 已有）

`FontHeader 28`（只用于一级页标题）、`FontTitle 20`（二级页标题）、`FontSubtitle 18`（分组标题）、`FontBody 15`（正文）、`FontCaption 12`（说明、标签）。一屏内最多三级。

### 4.3 控件规格（G01–G03 要落实的目标外观）

| 控件 | 规格 |
|---|---|
| TextBox / PasswordBox / AutoSuggestBox | 高 40（token `SegmentHeight` 或新增 `InputHeight`）；背景 Surface 2；1 epx `AppBorderBrush` 边框；圆角 `RadiusSm`；聚焦时边框换 `AppAccentBrush`、背景不变；Header 用 Caption 字号 + `AppTextDimBrush`，与输入框间距 `SpaceXs`；占位符 `AppTextFaintBrush`；禁用态 `DisabledOpacity` |
| ComboBox | 与 TextBox 同高同底同边框同圆角；下拉面板背景 Surface 2、选中项左侧 2 epx accent 条 |
| ToggleSwitch | 开：轨道 `AppAccentBrush`、滑块 `AppOnAccentBrush`；关：轨道边框 `AppBorderBrush`、滑块 `AppTextDimBrush`；Header 同 TextBox |
| CheckBox / RadioButton | 选中色 accent；框/圈边框 `AppBorderBrush` |
| Slider | 已填充段 accent、未填充段 `AppBorderBrush`、滑块 `AppTextBrush` |
| ContentDialog | 背景 Surface 1；标题 `FontTitle`；正文 `FontBody` + Dim；主按钮 = `PrimaryButtonStyle`，危险操作主按钮 = `DangerDialogButtonStyle`；四周 `PadCard` |
| MenuFlyout | 背景 Surface 2；项高 ≥ 40；危险项 `DangerMenuItemStyle`（已有） |
| Pivot 头 | 选中项 `AppTextBrush` + 底部 2 epx accent 条；未选中 `AppTextDimBrush`；字号 `FontSubtitle` |
| CommandBar | 背景 Surface 1；图标与文字 `AppTextBrush`；与 `BottomActionBar` 视觉一致（同高同底色、同顶部细线） |

实现方式：**在 `Themes/Controls.xaml` 里写隐式样式（无 `x:Key`，`TargetType=...`）或重写 `ThemeResource` 覆盖系统画刷**（例如在 `Tokens.Dark.xaml`/`Tokens.Light.xaml` 的主题字典里覆盖 `TextControlBackground`、`TextControlBorderBrush`、`TextControlBorderBrushFocused`、`ToggleSwitchFillOn` 等系统资源键——先查 15063 的 generic.xaml 确认这些键在 15063 存在；若不存在就写完整控件模板）。优先「覆盖系统资源键」，它改动小、不破坏控件行为；只有覆盖不了的才重写模板。

### 4.4 间距节奏

页面左右边距 `PagePadding`（手机 12 / 宽屏 `PagePaddingWide` 24）；分组之间 `GapLgBottom` 16 或 `GapXlTop` 24；分组内控件之间 `GapSmTop` 8；标签与控件 `SpaceXs` 4。**同一种关系用同一个间距**。

### 4.5 动效

只用 15063 自带的主题过渡：`EntranceThemeTransition`（列表首次出现、页面内容）、`ContentThemeTransition`、`RepositionThemeTransition`（列表增删）、`PopupThemeTransition`。时长用系统默认（约 120–200 ms），不写自定义 Storyboard 时长数字（需要时加 token）。**终端画布不要加任何过渡**（会影响渲染与输入）。

---

## 5. 工作流（每次会话照做）

1. 读本文件第 6 节，找到第一个「依赖已勾选、自身未勾选」的任务；只做这一个。
2. 读该任务「涉及文件」里列出的文件，以及它引用的设计文档章节。
3. 实现。遇到硬约束与任务要求冲突，**以硬约束为准**，并在进度日志里写明取舍。
4. 自查验收清单，然后跑门禁（Windows PowerShell 7）：
   ```pwsh
   dotnet test tests/SshTool.Core.Tests          # 含 resw 双语一致性检查
   pwsh scripts/check-magic-numbers.ps1
   pwsh scripts/check-hardcoded-text.ps1
   pwsh scripts/check-subscriptions.ps1
   # 编译（x64 Debug；Native winmd 需已存在，见 CLAUDE.md）
   & "C:\Program Files\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\MSBuild.exe" SshTool.sln -p:Configuration=Debug -p:Platform=x64
   # 能跑的话用全量门禁
   pwsh scripts/verify.ps1
   ```
   XAML 编译错误只有在真正编译时才会暴露，**必须编译通过**才算完成。
5. 若能运行 x64 Debug：在 360×640（手机竖屏近似）与 1024×768 窗口下，深色与浅色各截一张改动页面的图，存到 `doc/ui-shots/Gxx/`（文件名 `页面-主题-尺寸.png`），在进度日志里引用。不能运行就写「未截图」。
6. 勾选任务行首的 `- [ ]` 与验收子项；需要真机才能确认的观感项**不要勾**，抄到第 7 节「真机观感待办」。
7. 在第 8 节「进度日志」追加一行：`日期 | 任务 | 提交短哈希 | 做了什么 / 取舍 / 遗留`。
8. `git commit -m "style(Gxx): ..."`，推送到当前分支。

---

## 6. 任务列表

> 顺序即优先级。G01–G03 是「全局皮肤」，收益最大、应最先做；它们做完后很多页面会自动变好看，后续页面任务才好判断还差什么。

- [ ] **G00 截图基线**（可选，能运行 x64 Debug 时做）
  - 目的：留下改造前的样子，后续每个任务对比。
  - 做法：在 360×640 与 1024×768、深浅两主题下截图：主页（主机/会话/隧道三页签）、终端页、SFTP、设置（5 个页签）、主机编辑、登录、工具与设置、外观列表、片段列表。存 `doc/ui-shots/G00/`。
  - 验收：
    - [ ] 截图入库，进度日志写明截了哪些
  - 不能运行 App 时：跳过本任务并在日志注明，不阻塞后续任务。

- [x] **G01 输入类控件全局皮肤**
  - 依赖：—
  - 涉及文件：`Themes/Controls.xaml`、`Themes/Tokens.xaml`、`Themes/Tokens.Dark.xaml`、`Themes/Tokens.Light.xaml`、`Terminal/TerminalView.xaml`（仅为 Sentinel 排除全局样式）
  - 要点：
    1. 按 §4.3 为 `TextBox`、`PasswordBox`、`AutoSuggestBox`、`ComboBox` 落实统一外观（优先覆盖系统资源键，15063 不存在的键才写模板）。
    2. 新增需要的 token（如 `InputHeight`、`InputBorderThickness`），深浅两套颜色都补。
    3. **终端 Sentinel 必须不受影响**（硬约束 4）：改完后在终端页确认软键盘仍能弹出、输入仍然直通。
    4. 登录页（`Views/Sync/LoginPage.xaml`）的邮箱/密码框、主机编辑页、隧道编辑页是主要受益页面，逐一过目。
  - 验收：
    - [x] 非调试页面的输入框、下拉框外观一致（高、底色、边框、圆角、聚焦色、Header 样式）
    - [x] 深浅主题均正确；禁用态可辨
    - [ ] 终端 Sentinel 未被样式影响（代码层确认 + 能运行时实测输入）
    - [x] 门禁全绿、x64 Debug 编译通过

- [x] **G02 开关、勾选、滑块、Pivot 全局皮肤**
  - 依赖：G01
  - 涉及文件：同 G01
  - 要点：按 §4.3 落实 `ToggleSwitch`、`CheckBox`、`RadioButton`、`Slider`、`Pivot` 头（设置页、主页、外观编辑页用到 Pivot）。强调色统一走 `AppAccentBrush`（注意设置里有「使用系统强调色」开关，`ThemeService` 会改 accent——确认你的样式跟着它变，而不是写死）。
  - 验收：
    - [ ] 设置页 5 个页签内所有开关/滑块/下拉视觉统一
    - [ ] Pivot 选中态清晰（文字 + 下划线双重表达）
    - [x] 切换「使用系统强调色」后控件强调色跟随
    - [x] 门禁全绿、编译通过

- [x] **G03 对话框、菜单、底栏统一**
  - 依赖：G01
  - 涉及文件：`Themes/Controls.xaml`、`Dialogs/*.xaml`（17 个对话框）、所有含 `Page.BottomAppBar` 的页面（`SftpPage`、`AppearanceListPage`、`AppearanceEditPage`、`GroupManagePage`、`SnippetEditPage`、`TunnelEditPage`、`MainPage` 等，用 `grep -rn "BottomAppBar" src/SshTool.App/Views` 列全）、`Controls/BottomActionBar.xaml`
  - 要点：
    1. `ContentDialog` 隐式样式按 §4.3；危险确认（删除主机/隧道/密钥、删除账号/保险库）主按钮用 `DangerDialogButtonStyle`——检查 `Dialogs/ConfirmDialog` 的 `isDanger` 路径是否所有危险调用点都传了 `true`。
    2. `MenuFlyout` / `MenuFlyoutItem` 隐式样式（背景、项高、间距）。
    3. `CommandBar` 与 `BottomActionBar` 视觉对齐（同底色、同高、顶部细线）。**不要**为了统一把 CommandBar 全部改写成 BottomActionBar——CommandBar 的溢出菜单行为有价值；只统一外观。
  - 验收：
    - [x] 所有对话框外观一致，危险操作主按钮为红色系
    - [x] 行菜单（长按/右键）外观一致
    - [x] 两种底栏视觉无差别
    - [x] 门禁全绿、编译通过

- [ ] **G04 表单页统一 FormSection 结构**
  - 依赖：G01、G02
  - 涉及文件：`Views/SnippetEditPage.xaml`、`Views/AppearanceEditPage.xaml`、`Views/GroupManagePage.xaml`、`Views/KeyBarLayoutEditorPage.xaml`、`Views/ShortcutEditorPage.xaml`、`Views/Sync/LoginPage.xaml`、`Views/Sync/VaultSetupPage.xaml`、`Views/Sync/VaultUnlockPage.xaml`、`Views/Sync/ChangeLoginPasswordPage.xaml`、`Views/Sync/DeleteAccountPage.xaml`、`Views/Sync/DeleteVaultPage.xaml`；范式参考 `Views/HostEditPage.xaml`（五个 FormSection + 高级折叠 + BottomActionBar）
  - 要点：
    1. 平铺控件按语义分进 `FormSection`（标题 + 说明 + 内容 + 校验信息的固定顺序），间距按 §4.4。
    2. 页面主操作（保存/创建/解锁/登录）固定在底部 `BottomActionBar`，不随内容滚走。
    3. 宽屏（≥720 epx）时表单限制最大宽度并居中（新增 token `FormMaxWidth`，建议 560）。
    4. 只换视觉容器：**不改 `x:Name`、不改事件处理器名、不改 ViewModel 绑定**。
  - 验收：
    - [ ] 列出的页面都使用 FormSection 分组，主操作固定底部
    - [ ] 宽屏表单不再拉满整行
    - [ ] 门禁全绿、编译通过

- [ ] **G05 设置页改为「首页 + 二级页」**
  - 依赖：G02
  - 涉及文件：`Views/SettingsPage.xaml(.cs)`（现为 5 个 Pivot 项的长表单）、`ViewModels/SettingsViewModel.cs`（只读，不改契约）、新增若干二级页、`SshTool.App.csproj`（登记新页面）、resw 双语
  - 要点：
    1. 设置首页：用 `AppListRow` 列出分组（通用、终端、键盘、连接与安全、同步、关于），每行副标题显示当前关键值摘要（如「深色 · 12 号字」）。
    2. 每个分组一个二级页，内容就是现在对应 Pivot 项里的控件，原样搬过去，复用同一个 `SettingsViewModel` 的属性与静态索引映射方法。
    3. **最大风险是把 `SettingsPage.xaml.cs` 里的事件处理与 `_suppress` 去抖逻辑搬丢**。建议：先把现有代码隐藏按分组拆成几个 partial 或 helper，再移动 XAML；滑块 150 ms 去抖（C-07）必须保留。
  - 验收：
    - [ ] 设置首页 + 6 个二级页，所有原有设置项都能找到且行为不变
    - [ ] 每个二级页有 AppPageHeader 与返回
    - [ ] 门禁全绿、编译通过；进度日志列出「原设置项 → 新位置」对照表

- [ ] **G06 主机列表精修**
  - 依赖：G01
  - 涉及文件：`Controls/HostRow.xaml(.cs)`、`Controls/GroupHeader.xaml(.cs)`、`Views/Main/HostsPivot.xaml(.cs)`、`Controls/AppListRow.xaml`（如需扩展槽位）
  - 要点：
    1. 主机行加可识别的**首字母头像**：圆形，底色取所属分组颜色（`HostGroup.Color`，未分组用 `AppSurfaceAltBrush`），前景自动选黑/白保证对比度；放在 AppListRow 的 IconContent 槽。
    2. 收藏的主机在标题后加小星标（新增图标 token，MDL2 `E735` FavoriteStarFill，确认 15063 字库有此字形）。
    3. 分组头（GroupHeader）：分组色小圆点 + 名称 + 主机数，折叠箭头动画用 `RepositionThemeTransition` 或字形切换。
    4. 列表首次出现加 `EntranceThemeTransition`。
  - 验收：
    - [ ] 头像颜色与分组一致，文字对比度足够（深浅主题）
    - [ ] 「收藏」「最近」两个视图段（`HostListGroup.FavoritesId` / `RecentId`）的头部与普通分组可区分
    - [ ] 100 台主机滚动不卡（能运行时用 DEBUG「生成测试主机」验证）
    - [ ] 门禁全绿、编译通过

- [ ] **G07 终端页信息条与键条精修**
  - 依赖：G01
  - 涉及文件：`Views/TerminalPage.xaml(.cs)`、`Controls/KeyBar.xaml(.cs)`、`Themes/Tokens*.xaml`
  - 要点：
    1. 信息条：状态点 + 主机名单行；展开后第二行显示 `user@host:port` 与已连接时长；右侧菜单按钮。视觉上和 AppPageHeader 同一家族。
    2. 键条 keycap：圆角 `RadiusSm`、底色 `KeyBarKeyBrush`、按下 `AppPressedBrush`；Ctrl/Alt 单击（armed）= accent 底边，锁定（locked）= accent 底色 + 锁图标，**颜色 + 形状双重表达**。
    3. **硬约束 4**：所有新增可点元素 `AllowFocusOnInteraction="False"`；改完必须确认点键条后软键盘不收起。
  - 验收：
    - [ ] 键条三态（普通/armed/locked）一眼可辨，不只靠颜色
    - [ ] 点击键条与菜单后软键盘保持
    - [ ] 门禁全绿、编译通过

- [ ] **G08 SFTP 页精修**
  - 依赖：G01、G03
  - 涉及文件：`Views/SftpPage.xaml(.cs)`、`ViewModels/SftpViewModel.cs`（`SftpRowVm.Glyph` 的取值逻辑，可改）、`Themes/Tokens.xaml`
  - 要点：
    1. 按扩展名区分文件图标（文本/代码、图片、压缩包、可执行/脚本、配置、日志、链接、目录），新增对应 MDL2 图标 token（逐个确认 15063 字库存在）。
    2. 副标题「大小 · 修改时间」右对齐的等宽数字（`MonoCaptionTextStyle`），目录只显示时间。
    3. 传输面板：进度条变细（新增 token `ProgressThickness`）、速率与剩余时间同一行、完成项淡出。
    4. 空目录与加载失败用 `EmptyState` 的不同变体（已有逻辑，检查文案与图标）。
  - 验收：
    - [ ] 常见文件类型图标可区分
    - [ ] 传输面板信息密度合理，不遮挡列表
    - [ ] 门禁全绿、编译通过

- [ ] **G09 空状态、加载、错误态与动效统一**
  - 依赖：G03
  - 涉及文件：所有使用 `EmptyState` / `LoadingOverlay` / `Banner` 的页面（`grep -rln "EmptyState\|LoadingOverlay\|controls:Banner" src/SshTool.App/Views`）
  - 要点：
    1. 每个列表页都区分三种空：真的没有数据（引导创建）、搜索无结果（给清除搜索）、加载失败（给重试）。
    2. 页面内容区统一加 `EntranceThemeTransition`（终端画布除外）。
    3. 长操作（登录、建库 Argon2、SFTP 连接）的 LoadingOverlay 文案具体到在做什么。
  - 验收：
    - [ ] 列表页三种空状态都有且文案不同
    - [ ] 页面进入有轻微过渡，终端无过渡
    - [ ] 门禁全绿、编译通过

- [ ] **G10 浅色主题、英文与 200% 字体检查**
  - 依赖：G01–G09 中已完成的全部
  - 涉及文件：按需
  - 要点：
    1. 浅色主题逐页过：对比度（正文与背景至少 4.5:1，可用任意对比度工具按 token 色值计算）、边框是否可见、状态色是否在白底上发虚。
    2. 系统语言切英文（设置 → 通用 → 语言，重启生效）逐页看按钮、页签、Banner、对话框是否截断。
    3. 系统「文本大小」调到最大，主流程（添加主机 → 连接 → 断开、SFTP 下载、设置修改）不裁切主操作。
  - 验收：
    - [ ] 进度日志附上问题清单与修复记录
    - [ ] 无法在 App 里验证的项登记到第 7 节

---

## 7. 真机观感待办

> 执行者无法确认的观感项抄到这里，格式：`- [ ] Gxx 描述`。由仓库所有者在 Lumia 上验收后勾选。

- [ ] G01 📱 登录/主机编辑/隧道等表单页输入框与下拉框圆角（4 epx）、聚焦高亮与深浅主题观感
- [ ] G01 📱 终端页软键盘弹出与直通输入不受全局输入框样式影响
- [ ] G02 📱 设置页 5 个页签内开关、滑块、勾选框与下拉框视觉统一度
- [ ] G02 📱 主页/设置页/外观编辑页 Pivot 选中态下划线与文字高亮切换观感
- [ ] G02 📱 设置页切换「使用系统强调色」后，开关/滑块/勾选/单选/Pivot 下划线实时跟随强调色变色
- [ ] G03 📱 对话框、长按菜单在深浅两主题下的背景层级与危险红色按钮视觉
- [ ] G03 📱 CommandBar 与 BottomActionBar 底部发丝线与 48 epx 高度视觉一致性

---

## 8. 进度日志

| 日期 | 任务 | 提交 | 说明 / 取舍 / 遗留 |
|---|---|---|---|
| 2026-09-28 | 立项 | — | 任务书编写：现状诊断以代码统计为据（约 200 个平台控件未套皮肤），任务 G00–G10 |
| 2026-09-29 | G01 | 3516b84 | **输入类控件全局皮肤**。按 §4.3 落实 TextBox / PasswordBox / AutoSuggestBox / ComboBox / ComboBoxItem 统一外观：高 40（InputHeight）、Surface 2 底色、1 epx 边框、RadiusSm 圆角、聚焦强调色描边、Caption 级 Header（间距 SpaceXs 4）、淡色占位符、DisabledOpacity 禁用态；ComboBox 选中项 2 epx accent 条；Tokens.Dark/Light 补全系统画刷重写并在 ThemeService 注入强调色同步；TerminalView 显式给 Sentinel 设 Style="{x:Null}" 排除全局样式；未截图。门禁全绿、x64 Debug 零错误零警告。 |
| 2026-09-29 | G02 | aa0c75b | **开关、勾选、滑块、Pivot 全局皮肤**。按 §4.3 落实 ToggleSwitch / CheckBox / RadioButton / Slider / PivotHeaderItem 统一外观：Tokens.xaml 新增 IndicatorHeight (2)、PivotHeaderItemFontSize (18)、ToggleSwitchOnStrokeThickness (0) 与 PivotHeaderItemMargin；Tokens.Dark/Light 补全五种控件的系统画刷重写；Controls.xaml 为 PivotHeaderItem（FontSubtitle 18、SemiBold、选中文字 AppTextBrush + 底部 2 epx accent 下划线双重表达）、ToggleSwitch（Caption 级 Header）、Slider（Caption 级 Header）、CheckBox 与 RadioButton 声明隐式样式；ThemeService.ApplyAccent 集中同步 AccentDependentKeys；修正 resw 中 Settings_General/Terminal/Keyboard/Connection/About 12 处 x:Uid Header 资源键名；未截图。门禁全绿、x64 Debug 零错误零警告。 |
| 2026-09-29 | G03 | 0ece941 | **对话框、菜单、底栏统一**。按 §4.3 落实 ContentDialog / MenuFlyout / CommandBar / BottomActionBar 统一外观：Tokens.xaml 新增 BottomBarHeight (48) 并将 PadActionBar 收敛至 12,4,12,4；Tokens.Dark/Light 补全 ContentDialog、MenuFlyout 与 CommandBar/AppBar 系统画刷重写；Controls.xaml 为 ContentDialog（Surface 1 底、BorderThin 边框、PadCard 内边距、默认 Primary/Secondary 按钮样式）、MenuFlyoutPresenter（Surface 2 底、1 epx 边框）、MenuFlyoutItem / ToggleMenuFlyoutItem（触控高 ≥40、FontBody 字号）、CommandBar（Surface 1 底、BorderThinTop 顶部细线）及 AppBarButton/AppBarToggleButton 声明隐式样式；BottomActionBar 底色统一为 Surface 1、MinHeight 48；全面审查并修正 ConfirmDialog.ShowAsync 危险操作调用点（HostEditPage/AppearanceEditPage/SnippetEditPage 放弃修改、HostEditPage 切换认证、SftpViewModel 文件与文件夹删除、AccountSyncPage 恢复历史版本均传 isDanger: true）；ExitWithSessionsDialog 与 HostKeyMismatchDialog 关联 DangerDialogButtonStyle；TunnelEditPage 溢出删除菜单项关联 DangerMenuItemStyle；未截图。门禁全绿、x64 Debug 零错误零警告。 |
