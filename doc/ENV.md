# 开发与部署环境记录（X01）

> 本文件由 X01 任务产生。机器侧信息为 2026-09-17 实测；📱 手机侧信息待人工回填。

## 1. 构建机实测（2026-09-17）

| 项 | 要求（01-DESIGN §1.2） | 实测 | 结论 |
|---|---|---|---|
| 主力 IDE | VS2019 16.11（UWP + C++ v142 UWP + ARM 编译器） | **未安装**。已装：VS2017 15.9.83 Community（v141 14.16.27023，含 ARM/ARM64/UWP VC）、VS2026 18.10.1 Professional（含 UWP 工作负载；v142=14.29.30133 **无 ARM32**，v145=14.51.36231 **无 ARM32**，仅 arm64） | ⚠️ 见下方「工具链决策」 |
| Windows SDK（UAP 平台） | Target 10.0.17763 | 已装 UAP 平台：10.0.16299 / **10.0.19041** / 10.0.26100；**无 17763** | ⚠️ Target 改用 **10.0.19041**（仍支持 ARM32；26100 起 UWP 不支持 ARM32，禁用） |
| Windows Mobile Extensions for the UWP | 需要 | 已装 10.0.16299 / 10.0.19041 | ✅ 用 10.0.19041 |
| .NET SDK | 8.x（Core 测试） | 10.0.401（`C:\Program Files\dotnet`） | ✅ net8.0 目标经 NuGet 目标包可用 |
| CMake | ≥3.25（X06 起需要） | **未安装** | ⚠️ X06 前需人工安装 |
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

## 6. SP01 Spike 结论（进行中）

- 2026-09-17 宿主机：Newtonsoft.Json 12.0.3（netstandard1.4 目标）`JsonSpike.RoundTrip()` 全部 9 项检查通过（固定键序、int/bool/null/数组/嵌套对象、Unicode 往返、ulong 上限、时间格式）——`dotnet test` 覆盖；x64 Debug 与 ARM Release（.NET Native）构建通过。
- `Microsoft.NETCore.UniversalWindowsPlatform` 维持 **6.2.14** 锁定（`Directory.Build.props`），暂不降 5.4.x。
- 📱 待真机：ARM Release 包运行 SpikePage，核对报告 9 项全 PASS（重点：ulong 上限、固定键序、Unicode/emoji 往返在 .NET Native 下不丢字）。失败则按 D9 兜底降 5.4.x 重测并在此记录现象。
