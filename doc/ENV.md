# 开发与部署环境记录（X01）

> 本文件由 X01 任务产生。机器侧信息为 2026-09-17 实测；📱 手机侧信息待人工回填。

## 1. 构建机实测（2026-09-17）

| 项 | 要求（01-DESIGN §1.2） | 实测 | 结论 |
|---|---|---|---|
| 主力 IDE | VS2019 16.11（UWP + C++ v142 UWP + ARM 编译器） | **未安装**。已装：VS2017 15.9.83 Community（v141 14.16.27023，含 ARM/ARM64/UWP VC）、VS2026 18.10.1 Professional（含 UWP 工作负载；v142=14.29.30133 **无 ARM32**，v145=14.51.36231 **无 ARM32**，仅 arm64） | ⚠️ 见下方「工具链决策」 |
| Windows SDK（UAP 平台） | Target 10.0.17763 | 已装 UAP 平台：10.0.16299 / **10.0.19041** / 10.0.26100；**无 17763** | ⚠️ Target 改用 **10.0.19041**（仍支持 ARM32；26100 起 UWP 不支持 ARM32，禁用） |
| Windows Mobile Extensions for the UWP | 需要 | 已装 10.0.16299 / 10.0.19041 | ✅ 用 10.0.19041 |
| .NET SDK | 8.x（Core 测试） | 10.0.401（`C:\Program Files\dotnet`） | ✅ net8.0 目标经 NuGet 目标包可用 |
| CMake | ≥3.25（X06 起需要） | **未安装**（verify.ps1 回退用 `tools/vcpkg/downloads/tools/cmake-4.4.0`，免装） | ✅ X06 已解决 |
| Git | 需要 | 2.54.0.windows.1 | ✅ |
| Perl | OpenSSL 备用构建 | cygwin perl 5.42.2 | ⚠️ OpenSSL 官方推荐 Strawberry Perl，SP02 时验证 |
| Node | 20+（S04 sync-vectors） | v24.15.0 | ✅ |
| WinAppDeployCmd | 部署到手机 | `C:\Program Files (x86)\Windows Kits\10\bin\10.0.19041.0\x86\WinAppDeployCmd.exe`（26100 亦有一份） | ✅ |

### 工具链决策（2026-09-17，替代 VS2019 方案）

- **C# 全部工程（App / Core / Tests）**：用 **VS2026 MSBuild**（`C:\Program Files\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\MSBuild.exe`）构建；UWP Target **10.0.19041** / Min **10.0.15063**。
- **C++/CX 原生组件（SshTool.Native）**：必须产出 **ARM32**，而 VS2026 的 v142/v145 均无 ARM32 编译器 → 使用 **v141 工具集**（来自并存的 VS2017 15.9，含 ARM + UWP VC 支持），即 `PlatformToolset=v141`。若 VS2026 MSBuild 不能解析并存 VS2017 的 v141，则回退为用 VS2017 MSBuild（`C:\Program Files (x86)\Microsoft Visual Studio\2017\Community\MSBuild\15.0\Bin\MSBuild.exe`）单独构建 Native。
- 该决策已回写 `01-DESIGN.md §1.2`；若日后补装 VS2019 16.11 + SDK 17763，可回到原方案。

## 2. 手机侧（👤 待回填）

- [ ] 手机型号 / OS build（设置 → 关于，预期 10.0.15254.x）：____
- [ ] 已开启：开发人员模式 ☐  设备发现 ☐  设备门户 ☐
- [ ] 手机 IP / 配对 PIN 获取方式已验证：____

## 3. 部署命令（参考）

```bat
:: 列出可见设备（USB 或同网段）
"C:\Program Files (x86)\Windows Kits\10\bin\10.0.19041.0\x86\WinAppDeployCmd.exe" devices

:: 安装（dependencies 目录含 VCLibs、.NET Native Runtime/Framework ARM 依赖包）
"C:\Program Files (x86)\Windows Kits\10\bin\10.0.19041.0\x86\WinAppDeployCmd.exe" install -file "<appx路径>" -ip <手机IP> -pin <PIN>
:: 若依赖未自动安装，逐个：
"...\WinAppDeployCmd.exe" install -file "<依赖包.appx>" -ip <手机IP> -pin <PIN>
```

配对：手机 设置 → 更新和安全 → 开发者选项 → 打开「设备发现」→ 点「配对」获取 PIN。

## 4. 待真机/人工验证项

- [ ] 📱 空白应用（X02 的 MainPage 即可充当）在 Lumia 950 启动成功
- [ ] VS2026 能否直接部署/调试到 W10M：未测（部署默认走 WinAppDeployCmd）
- [ ] 📱 ARM Release（.NET Native）包真机启动

## 5. 踩坑记录

- VS2026（18.10）起 MSVC 工具集不再提供 32 位 ARM 目标（v142/v145 均只有 x86/x64/arm64/onecore）；ARM32 UWP C++ 只能使用 v141（VS2017）或 VS2019 的 v142。本机无 VS2019，故 Native 固定 v141。
- Windows SDK 10.0.26100 的 UAP 平台已不支持 ARM32 目标；本工程 Target 固定 10.0.19041，切勿升级。
- **VS2026 无法构建任何 C++/CX UWP 工程**（无 UWP VC 组件，且找不到 v141 的 Windows Store 生成工具）→ 构建为两段式：VS2017 MSBuild 构建 `SshTool.Native.vcxproj`，VS2026 MSBuild 构建 `SshTool.sln`（sln 中 Native 无 Build.0 条目；App 以 winmd 引用其产物，HintPath 含 `$(NativePlatform)` 处理 x86→Win32 映射）。
- vcxproj 要点：`Configuration` 属性组（PlatformToolset）必须写在 `Microsoft.Cpp.props` 导入**之前**；`WindowsAppContainer=true` 必须显式写（否则 /ZW 找不到 platform.winmd）；UWP 的 `Platform.Default.props` 会覆盖 Globals 里的 `OutDir`（它只认命令行传入），固定输出目录的写法必须放在 `Microsoft.Cpp.props` 导入**之后**。
- 经典格式 UWP C# csproj：PackageReference 需要 `RuntimeIdentifiers` 含字面 `win10`（旧版 ResolveNuGetPackageAssets 按基础 RID 查询）；每个配置需显式 `PlatformTarget`（否则打包任务 WireUpCoreRuntime 报 MSB4044）；清单必须有 `mp:PhoneIdentity`（否则 APPX1673）。
- **不要把 net8 测试工程放进 sln**：VS 解决方案级 `-t:Restore` 会用旧框架解析器把 `net8.0` 写成 `.NETFramework,Version=v8.0`，损坏其 project.assets.json。Tests 由 `dotnet test` 驱动（若被损坏：`rm -rf tests/*/obj` 后重跑）。
- Git Bash 里调用 MSBuild 必须用 `-p:` 短横线开关，`/p:` 会被 MSYS 路径转换吞掉。
- **VS2026 AppxPackage targets 增量构建 bug**：开 bundle（默认）且存在多语言资源分包（`language-en.appx`）时，第二次起的增量 x64 Debug 打包会丢 `PackageLayout\entrypoint\SshTool.App.exe`（MakeAppx 0x80070003 / mapping file line 135；2026-09-18 又见一次紧跟 `-t:Rebuild` 之后的 0x80070002，重跑即过，mapping 里 139 项源文件实际都在）。处置：`AppxBundle=Never`（散装 appx 正是 WinAppDeployCmd 旁加载所需），X06 实测三连构建稳定。首发现场：verify.ps1 门禁。
- native C/C++ 源码一律 UTF-8（无 BOM）；MSVC 工程必须加 `/utf-8`（已在 native/tests CMakeLists 设置），否则 GBK 区域下报 C4819 且可能吞字符导致诡异编译错误。
- pwsh 脚本在 Git Bash 里 `| tail` 时退出码被 tail 覆盖，验证脚本退出码需 `set -o pipefail`。
- **VS 里构建报 `ilc.exe 未能运行……ilclog.csv 正由另一进程使用`（LoggerBasedExecTask）**：命令行构建与 VS 撞同一配置。MSBuild 默认 **node reuse**，命令行构建结束后仍留常驻 `MSBuild.exe` 节点攥着 `obj\<Plat>\Release\ilc\` 下的文件，VS 再构建同一配置即被占用（.NET Native 的 ilc 尤其明显）。处置：命令行一律加 `-nr:false`（`scripts/verify.ps1` 已全部加上，2026-09-18）；已中招则 `Get-Process MSBuild | Stop-Process -Force`（别杀 devenv）+ 删 `obj\<Plat>\Release\ilc` 后重建。另：同一配置不要 VS 与命令行同时构建。
- **Release 包里进不去调试页（SpikePage / TokenGalleryPage）**：入口编译开关是 `DEBUG_PAGES`，由 csproj 的 `EnableDebugPages` 决定（Debug 默认开）。SP01/X03 的 📱 验收必须在 Release 包上做——**只有 Release 才 `UseDotNetNativeToolchain=true`**，Debug（含 ARM Debug）走 CoreCLR，验不到 .NET Native 相关风险。出包：`-p:Configuration=Release -p:Platform=ARM -p:EnableDebugPages=true`（2026-09-18 加入）。
- **真机加载某个 XAML 时 `XamlParseException 0x802B000A`（Failed to assign to property …）**：该属性的 API 契约高于 `TargetPlatformMinVersion=15063`（contract 4.0）。首例：`Controls/Banner.xaml` 的 `Grid.ColumnSpacing`（需 contract 5.0 / 1709）。XAML 里无法用 `ApiInformation` 守卫，只能换等价写法——列/行间距改用子元素 `Margin`（token `GapSmLeft`）。编译期其实有 `WMC0151` 警告，但只是警告，且桌面 x64 （19041）跑起来不崩，只在 W10M 真机上炸，所以 `scripts/verify.ps1` 步骤⑤ 已把 WMC0151 升级为门禁失败（2026-09-18 加入，并用临时改回 `ColumnSpacing` 实测能拦住）。
- **运行到激活 `SshTool.Native` 时 `FileNotFoundException 0x8007007E`（找不到指定的模块）**：不是 winmd/DLL 缺失（`SshTool.Native.dll` 一直在包里），而是缺 C++ 运行时框架依赖。Native 以裸 `<Reference>` 引 winmd（非 ProjectReference），MSBuild 不会自动注入 VCLibs，三个配置的 AppxManifest 都没有 `Microsoft.VCLibs.140.00[.Debug]` 依赖（Debug 侧 DLL 导入 `vccorlib140d_app.dll`/`MSVCP140D_APP.dll`/`VCRUNTIME140D_APP.dll`/`ucrtbased.dll`，Release 侧同名非 d 版）。修法（2026-09-18，App csproj 两处）：① 显式 `<SDKReference Include="Microsoft.VCLibs, Version=14.0" />`；② `<AppxExcludeArmFrameworkSdkPackagesFromLayout>false</...>`——VS2026 该属性默认 `true`（ARM32 已被其放弃），会把 ARM 框架包排除出 `_Test\Dependencies\`，真机旁加载时装不上 VCLibs。实测三配置的 manifest 依赖与 `Dependencies\arm\` 均已补齐（ARM Release 之前同样缺，真机装了也会崩）。
- **VS 里选中未预建过的配置 → `RequireNativeWinmd` 报「缺少 SshTool.Native.winmd」（此前是 CS0234「命名空间 SshTool.Native 不存在」）**：winmd 只能由 VS2017(v141) 两段式预建，`SshTool.Native/bin/<Platform>/<Configuration>/` 里有哪组就只能构建哪组。`bin/` 不入库，换机/清理后需重建。本机当前已建 `x64/Debug`（日常）、`ARM/Release`（真机打包）、`ARM/Debug`（真机调试，2026-09-18 补建，C# 侧 ARM Debug 打包实测通过，无 .NET Native 编译，比 ARM Release 快得多）。缺别的组合时照报错里的命令用 VS2017 MSBuild 单独构建该 vcxproj 即可，或跑 `scripts/verify.ps1`（只覆盖 x64 Debug，`-Arm` 追加 ARM Release）。注意 csproj 里 `$(MSBuildProjectDir)` 在某些加载路径下会求值为空（实测 MSBuild 18.10），路径判断一律用 `$(MSBuildThisFileDirectory)`。

## 6. SP01 Spike 结论（已结，2026-09-18）

- 2026-09-17 宿主机：Newtonsoft.Json 12.0.3（netstandard1.4 目标）`JsonSpike.RoundTrip()` 全部 9 项检查通过（固定键序、int/bool/null/数组/嵌套对象、Unicode 往返、ulong 上限、时间格式）——`dotnet test` 覆盖；x64 Debug 与 ARM Release（.NET Native）构建通过。
- 2026-09-18 **真机（Lumia 950，ARM Release / .NET Native 包）：9 项全 PASS**，与宿主机结果逐项一致。
  → D9 成立：`JsonTextReader`/`JsonTextWriter`/`JObject` 这条路子在 .NET Native 下安全；反射式 `SerializeObject<T>` 仍然禁止（未验且需 rd.xml）。
- `Microsoft.NETCore.UniversalWindowsPlatform` 维持 **6.2.14** 锁定（`Directory.Build.props`），**无需降 5.4.x**。
- 复验方法：`-p:Configuration=Release -p:Platform=ARM -p:EnableDebugPages=true` 出包 → MainPage「SP01 JSON Spike」→ 报告首行会自报 DeviceFamily/OS/架构/工具链。
- ~~📱 待真机~~ → 已于 2026-09-18 完成，见上一条。

## 7. SP02 Spike 结论（2026-09-17）

- **方案 A（vcpkg）成功**：OpenSSL **3.6.3**（vcpkg ref `2026.07.29`）四个 triplet 全部编成静态库 → `native/prebuilt/{x64-windows-static,x86-uwp,x64-uwp,arm-uwp}/`（各含 libcrypto.lib + libssl.lib + include + VERSION.txt，不入库）。一键脚本 `scripts/build-openssl.ps1`（幂等，已重跑多次验证）。方案 B（perl Configure + nmake）未启用。
- UWP 三架构走 overlay triplets `native/triplets/*-uwp-v141.cmake`（v141 + 动态 CRT + 静态库）。**ARM 独有补丁**：SDK ≥ 22621 删除 `um/arm`/`ucrt/arm`，vcpkg 注入的 LIB 取自最新 SDK（26100）导致探测链接 LNK1104；triplet 内用 `VCPKG_LINKER_FLAGS` 以 **8.3 短路径**补 19041 库目录（带空格长路径的引号会被 -D 传递剥掉）。8.3 名按机器生成，换机需重查（`native/NATIVE-BUILD.md` §4 踩坑 2）。
- Native 组件（v141）链接验证：`libcrypto.lib` 需配 `crypt32.lib`（`CertOpenSystemStoreW`）。`NativeInfo::OpenSslVersion()` 已加，MainPage 第三行显示。x64 Debug 与 ARM Release（.NET Native 打包）全链路构建通过。
- 耗时参考：x64-windows-static 7.4 min，uwp 三架构各约 3.4–3.9 min；产物 libcrypto 约 64–90 MB（含调试信息）。
- 📱 待真机：Lumia 上启动应用确认 MainPage 显示 `OpenSSL: 3.6.3 ...` 字样。

## 8. SP04 Spike 结论（进行中，2026-09-18）

- **Win2D.uwp 1.26.0 可用**：`TargetPlatformMinVersion=15063` 下构建/打包无版本告警；ARM Release 包内 `Microsoft.Graphics.Canvas.dll` 实测为 **ARM32**（dumpbin `1C4 machine (ARM)`，32 bit word machine）。版本锁在 `Directory.Build.props` 的 `Win2DVersion`。
  - 选 1.26.0 而非 1.28.3：1.26.0 原生库在 `runtimes/win10-arm`（UWP 工具链认的 RID），1.27 起改为 `runtimes/win-arm`；两版都有 ARM32 二进制，但新 RID 在 UWP 工程上未验证。
  - Win2D **同样依赖 VCLibs**：若 AppxManifest 缺 `Microsoft.VCLibs.140.00[.Debug]`，它会和 SshTool.Native 一样 0x8007007E（依赖已于本日修复）。
- 体积：加入 Win2D + 两款字体后 ARM Release 包 3.4 MB → **4.8 MB**（Canvas.dll 1.70 MB，两款 ttf 共 0.53 MB）。
- 字体：JetBrains Mono v2.304，`scripts/fetch-fonts.ps1` 按固定 URL + SHA256 抓取（OFL.txt 一并入包），引用名 `ms-appx:///Assets/Fonts/JetBrainsMono-Regular.ttf#JetBrains Mono`。
- **2026-09-18 真机首轮（Lumia 950，ARM Release/.NET Native）**——逐格绘制版本：

  | 负载 | 网格 | 实例 | tick/s | 平均绘制 | 对 30 fps 预算 |
  |---|---|---|---|---|---|
  | 全屏逐格重绘 | 48×30 | 单 | 15.9 | 99–105 ms | ✘ 差 3 倍 |
  | 全屏逐格重绘 | 88×24 | 单 | 12 | 138–152 ms | ✘ 差 4–5 倍 |
  | 每帧 3 行脏行 | 48×30 | 单 | 42–53 | — | ✔ |
  | 每帧 3 行脏行 | 48×30 | 双 | 24–27 | — | ✘ |
  | 每帧 3 行脏行 | 88×24 | 单 | 30 | 46–50 ms | ⚠ 卡线 |
  | 每帧 3 行脏行 | 88×24 | 双 | 18 | ~50 ms | ✘ |

  **读数注意**：上表 tick/s 是 `CompositionTarget.Rendering` 回调频率，而 `CanvasControl` 跟不上时会合并 `Invalidate`，
  所以 tick/s 高于实际重绘次数（88×24 脏行报 30 tick/s 但平均绘制 46–50 ms，实绘只有 ~20 次/秒）。
  该仪表已修：页面现在同时报 `tick/s` 与 `draw/s`（Draw 实际调用次数），以 `draw/s` 与平均绘制耗时为准。
- **初步结论**：§7.3 规定的「行内 run 合并 + 行缓存 + 只重绘脏行」不是优化项而是**必需项**——逐格 `FillRectangle`+`DrawText`
  在 ARM32 上约 50–70 µs/格，48×30 就要 ~100 ms/帧。已给压测页补「全屏 run 合并」负载以量化 §7.3 真实画法的成本。
- 📱 待真机（第二轮，用页面上的「跑全矩阵并导出」一键完成）：4 负载（逐格/run 合并/3 脏行/静止）× 2 网格 × 单/双实例的
  `draw/s` 与平均绘制耗时；以及页面报出的可用中文回退字体名。报告自动落 `LocalState\spike-reports\sp04-render-*.txt`、
  复制到剪贴板、并写入 `logs\app.log`。数字回来后回写 `01-DESIGN.md` D5、§7.4。

---
