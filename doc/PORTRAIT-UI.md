# 竖屏 UI 重设计（opt/full-pass）

目标机型：Lumia 950 / W10M（竖屏有效分辨率约 360×640 epx），最低 10.0.15063，C# 7.3。
原则：**只用 15063 已有能力**，横屏/宽屏（Continuum）布局不回退。

## 1. 15063 约束与替代方案

| 想要的效果 | 高版本 API | 15063 不可用的后果 | 本次替代 |
|---|---|---|---|
| 亚克力毛玻璃 | `AcrylicBrush`（16299） | XamlParseException | 半透明 `SolidColorBrush`（`AppGlassBrush`，约 85% 不透明）+ 发丝描边 |
| Reveal 高光 | `RevealBrush`（16299） | 同上 | 卡片顶部偏亮的 `LinearGradientBrush`（`AppCardBrush`）+ 低透明白色描边 |
| 间距 | `Grid.ColumnSpacing` / `StackPanel.Spacing`（16299） | 同上 | 一律用 Margin token（`GapMdLeft` 等） |
| 动画 | Composition 隐式动画（部分 14393+） | — | 只用 `EntranceThemeTransition` / `AddDeleteThemeTransition` / `RepositionThemeTransition` |
| 竖/横屏切换 | — | — | `AdaptiveTrigger MinWindowHeight`（`TallBreakpoint`=560）与原有 `MinWindowWidth`（`WideBreakpoint`=720） |

## 2. 新增 token（Themes/）

- 画刷（Dark/Light 各一份，键集合由 `ThemeTokenTests` 守护一致）：
  `AppCardBrush`（渐变卡底）、`AppCardPressedBrush`、`AppCardSelectedBrush`、`AppCardStrokeBrush`、
  `AppGlassBrush`、`AppGlassStrokeBrush`、`AppHeaderGlowBrush`（页头斜向辉光）、
  `AppAccentSoftBrush`（强调色 16% 叠层；`UseSystemAccent` 时 ThemeService 按 `AccentSoftOpacity` 跟随系统强调色）、
  `KeyBarGradientBrush`、`KeyBarKeyIdleBrush`、`KeyBarKeyStrokeBrush`。
- 尺寸（Tokens.xaml）：`TallBreakpoint`、`RadiusLg`、`CardMargin`、`CardListPadding`、
  `CardAccentStrip*`、`GapMdLeft`、`PadPillField`、`KeyBarPad`、`KeyBarRowGap`、
  `KeyBarExtraRowHeight`、`KeyBarExtraKeyMinWidth`、`TerminalGlassBarHeight`、`AccentSoftOpacity`。
- 样式（Controls.xaml）：`CardBorderStyle`、`CardListViewItemStyle`、`PillFieldBorderStyle`、
  `GlassBarGridStyle`、`TerminalGlassButtonStyle`（不抢哨兵焦点，§7.5）、`AccentCircleButtonStyle`。

## 3. 页面改动

- **主页**：页头背后叠 `AppHeaderGlowBrush` 辉光（不拦截点击）。
- **主机页**：搜索框放进胶囊容器（内部 TextControl 画刷就地透明）；新建按钮改强调色圆钮；
  快速连接行改强调色叠层圆角块，展开卡片用卡片材质 + 进场动画；主机行 `AppListRow.IsCard=True`
  变成圆角渐变卡片（按压/选中态用 `AppCardPressedBrush`/`AppCardSelectedBrush`），头像加发丝环；
  列表系统高亮改透明，条目进场错峰 + 增删/位移动画。
- **会话页**：会话卡片 = 左侧状态色条（`SessionStateVisuals.BrushKey`：已连接=Success、连接/认证/重连=Warning、
  错误=Danger、其余=Faint）+ 强调色终端图标块 + 标题/状态文字（状态文字同色，颜色不是唯一信息）+ 圆形关闭钮
  （无障碍名改走 resw `SessionsPivot_CloseButton`）；恢复提示改卡片。
- **终端页**：信息条/查找条改玻璃条（`GlassBarGridStyle`），单行高 44→40 epx；图标按钮透明底；
  竖屏（窗口高 ≥560）`TallState` 打开键条第二行。
- **键条**：底板纵向渐变、键帽渐变 + 发丝描边 + `RadiusMd`；第二行（`KeyBar.ShowExtraRow`）放
  `KeyBarLayout.ExtraRowDefaultString`（Shift、`_ : ; ' " \` < > [ ] { } \`、Del），并由
  `KeyBarLayout.ExtraRowFor(主行布局)` 去重，保证同一 id 只出现一次。第二行矮一档（34）、窄一档（36）。
  外层高度变化由 `TerminalPage.UpdateKeyBarLift` 读 `ActualHeight` 自动适配 SIP 抬升。

## 4. 已知限制 / 真机待验证

- `AppHeaderGlowBrush` 是固定色渐变，**不跟随系统强调色**（`AppAccentSoftBrush` 跟随）。
- 会话状态色由转换器在绑定时解析，运行中切换深浅主题后需状态变化或重进页面才刷新。
- 查找条 `EntranceThemeTransition` 在 Visibility 切换时是否重放以真机为准。
- 竖屏第二行键常驻会让 SIP 弹出时终端可视行减少约 2 行（38 epx）；如嫌挤可在后续加设置开关。
- 以上全部未在真机/模拟器上运行过（本环境无法构建 UWP），需在 Lumia 950 上逐项目测：
  深色/浅色、系统强调色开关、横竖屏切换、Continuum 宽屏、SIP 弹出时键条抬升、键条第二行修饰键三态。
