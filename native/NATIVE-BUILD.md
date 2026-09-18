# 原生依赖构建手册（Windows/UWP）

> 依据：`doc/01-DESIGN.md` D2/D16、`doc/04-TASKS.md` SP02/N01。
> 鸿蒙端对应文档（仅版本与裁剪思路参考）：`ssh_client_ohos/docs/NATIVE-BUILD.md`。
> 初版日期：2026-09-17；N01 于 2026-09-18 完成。

## 1. 方案与结论

- **OpenSSL（D16）**：方案 A —— **vcpkg**（固定 ref `2026.07.29`，浅克隆于 `tools/vcpkg/`，不入库）。
  - UWP 三架构（arm/x86/x64）：overlay triplets `native/triplets/*-uwp-v141.cmake`
    （静态库 + 动态 CRT + `VCPKG_PLATFORM_TOOLSET=v141` + `VCPKG_VISUAL_STUDIO_PATH=VS2017`）。
    原因：VS2026 的 v142/v145 已移除 ARM32 目标，ARM32 只能由并存 VS2017 的 v141 提供（`doc/ENV.md`）。
  - 宿主机测试用：`x64-windows-static`（vcpkg 内置 triplet，VS2026 默认工具集）。
    该 triplet 为**静态 CRT（/MT）**；`native/tests/CMakeLists.txt` 已通过
    `CMAKE_MSVC_RUNTIME_LIBRARY` 将 Core、三方库、GoogleTest 与测试程序统一为 `/MT`（Debug 为 `/MTd`）。
  - 方案 B（vcpkg 失败时备用，未启用）：OpenSSL 源码 `perl Configure VC-WIN32-ARM-UWP no-shared no-tests no-apps no-docs no-engine no-legacy` + `nmake`；x86/x64 用 `VC-WIN32-ONECORE` / `VC-WIN64A-UWP`；3.x 不行再试 1.1.1w。
- **libssh2 1.11.1 / libvterm 0.3.3 / phc-winner-argon2 20190702（N01）**：源码直接编进 native 组件。
  `scripts/fetch-third-party.ps1` 使用固定官方 URL 与 SHA256 拉到被忽略的 `native/third_party/`；
  `native/core/NativeCore.vcxitems` 是 UWP 的唯一源文件清单，宿主 CMake 使用相同选择规则。
  libvterm 使用官方 **release tar**，其中已带 `src/fullwidth.inc` 与 `src/encoding/{uk,DECdrawing}.inc`，
  因而新机器不需要 Perl 生成编码表。Argon2 固定 `ref.c`，明确不编 `opt.c`，并定义
  `ARGON2_NO_THREADS`，避免 SIMD 与 UWP 线程 API 差异。

## 2. 一键命令

```pwsh
# OpenSSL 四个 triplet 全量（幂等可重跑；产物在 native/prebuilt/，不入库）
pwsh scripts/build-openssl.ps1

# 只构建某个：pwsh scripts/build-openssl.ps1 -Triplets arm-uwp-v141

# 三份固定版本 C 源码（已有则跳过；-List 查看状态，-Force 校验后重抓）
pwsh scripts/fetch-third-party.ps1

# 全量门禁：Core + native 宿主测试 + App x64；再追加 ARM Release
pwsh scripts/verify.ps1
pwsh scripts/verify.ps1 -Arm
```

只验证 N01 的宿主依赖时（未把 CMake 加入 PATH 也可直接由 `verify.ps1 -Quick` 自动找到
`tools/vcpkg/downloads/tools/cmake-*`）：

```pwsh
pwsh scripts/verify.ps1 -Quick
```

## 3. 产物布局

```
native/prebuilt/
├── arm-uwp/              lib/libcrypto.lib (+libssl.lib)  include/openssl/*  VERSION.txt
├── x86-uwp/              同上
├── x64-uwp/              同上（SshTool.Native 按平台经 PrebuiltTriplet 属性引用）
└── x64-windows-static/   宿主机测试用（native/tests CMake 使用）
```

第三方源码布局（均不入库）：

```
native/third_party/
├── libssh2/              1.11.1；排除 agent_win.c、os400qc3.c
├── libvterm/             0.3.3 release tar；编码 .inc 已生成
└── argon2/               20190702；argon2/core/encoding/ref/thread/blake2b
```

`scripts/fetch-third-party.ps1` 在移动目录前检查探针文件，SHA 不符会删除下载缓存并立即失败。
源码有意不提交；可复现性由固定 URL、SHA、顶层目录与编译清单保证。源码补丁结论见
`native/third_party/PATCHES.md`。

## 4. 构建拓扑与逐项验证

- UWP：`src/SshTool.Native/SshTool.Native.vcxproj` 导入 `native/core/NativeCore.vcxitems`。
  所有第三方 C 文件关闭 PCH 与 C++/CX；OpenSSL 从 `native/prebuilt/<triplet>` 静态链接。
- 宿主：CMake 将同一批三方源码编为 `native_dependencies_host`，链接
  `native/prebuilt/x64-windows-static/lib/libcrypto.lib`，再运行 GoogleTest。
- 两份名为 `encoding.c` 的源分别写到 `obj/.../libvterm/` 和 `obj/.../argon2/`，避免 MSBuild
  对象文件重名覆盖。

N01 宿主测试共有三条依赖冒烟用例：

1. `libssh2_version(0) == "1.11.1"`；
2. 24×80 libvterm 写入 `hi`，前两个屏幕单元分别为 `h`、`i`；
3. Argon2id 对 RFC 9106 §5.3（32 KiB、3 passes、4 lanes、含 secret/ad）输出固定 32 字节 tag。

单独构建 UWP native 三架构：

```pwsh
$msbuild = 'C:\Program Files (x86)\Microsoft Visual Studio\2017\Community\MSBuild\15.0\Bin\MSBuild.exe'
& $msbuild src/SshTool.Native/SshTool.Native.vcxproj -p:Configuration=Debug -p:Platform=x64 -nr:false
& $msbuild src/SshTool.Native/SshTool.Native.vcxproj -p:Configuration=Debug -p:Platform=Win32 -nr:false
& $msbuild src/SshTool.Native/SshTool.Native.vcxproj -p:Configuration=Release -p:Platform=ARM -nr:false
```

若 VS2017 安装目录不同，用 `vswhere -version '[15.0,16.0)' -requires Microsoft.Component.MSBuild
-find 'MSBuild\**\Bin\MSBuild.exe'` 获取路径；`verify.ps1` 已自动执行这一步。

## 5. 实测记录

| triplet | 结果 | 耗时 | 产物大小（libcrypto / libssl） | 备注 |
|---|---|---|---|---|
| x64-windows-static | ✓ | 7.4 min | 64.0 / 13.4 MB | VS2026 默认工具集，/MT |
| x86-uwp-v141 | ✓ | 3.9 min | 86.5 / 17.2 MB | 一次通过 |
| x64-uwp-v141 | ✓ | 3.4 min | 90.3 / 17.7 MB | 一次通过 |
| arm-uwp-v141 | ✓ | 3.9 min | 90.2 / 17.8 MB | 需 triplet 内补 /LIBPATH（踩坑 1/2） |

均为 OpenSSL **3.6.3**（vcpkg `2026.07.29` port）。x64 Debug / ARM Release 全链路（Native + sln + App 打包）通过。

N01（2026-09-18）：宿主三条依赖测试全部通过；`SshTool.Native` 的 x64 Debug、Win32 Debug、
ARM Release 均以 VS2017 v141 构建成功。libvterm 0.3.3 的 C99 指定初始化器在 v141 实测可编译，
无需改源码；只在工程侧关闭 CRT 弃用诊断并禁用 `parser.c` 的 C4703 误报。

踩坑：

1. **SDK ≥ 22621（本机 26100）已删除 `um/arm` 与 `ucrt/arm` 目录**，vcpkg 给链接探测注入的 `LIB` 取自最新 SDK → ARM 探测阶段 `LNK1104 找不到 WindowsApp.lib`（x86/x64 因 26100 仍有对应目录而"恰好"成功）。`VCPKG_CMAKE_SYSTEM_VERSION=10.0.19041.0` 只影响 CMake 侧 rc/mt 选择与 WINVER，**不改 vcpkg 注入的 LIB**；必须在 `arm-uwp-v141.cmake` 里用 `VCPKG_LINKER_FLAGS` 显式 `/LIBPATH` 补 19041 的 `um/arm` 与 `ucrt/arm`。
2. **`VCPKG_LINKER_FLAGS` 不能含带空格路径**：值经 vcpkg → CMake `-D` 传递时内嵌引号被剥掉，`C:/Program Files ...` 被截断成 `/LIBPATH:C:/Program`。必须用 **8.3 短路径**。8.3 名按机器生成，本机为 `C:\PROGRA~2\WI3CF2~1 = C:\Program Files (x86)\Windows Kits`、`100190~1.0 = 10.0.19041.0`；换机用 `dir /x` 或 `(New-Object -ComObject Scripting.FileSystemObject).GetFolder(<路径>).ShortPath` 重查并改 triplet。
3. Native 组件链接 `libcrypto.lib` 需同时加 **`crypt32.lib`**（OpenSSL winstore 证书存储代码引用 `CertOpenSystemStoreW`），否则 `LNK2019 __imp_CertOpenSystemStoreW`。
4. vcxproj 的 XML 文本节点里路径不得断行（手滑在 `..\..\native\...` 中引入换行会让 include/lib 目录碎成两段，cl 随后把 /AI 的带空格 SDK 路径当源文件，报一堆 D9024/D9027 警告）。
