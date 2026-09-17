# 原生依赖构建手册（Windows/UWP）

> 依据：`doc/01-DESIGN.md` D2/D16、`doc/04-TASKS.md` SP02/N01。
> 鸿蒙端对应文档（仅版本与裁剪思路参考）：`ssh_client_ohos/docs/NATIVE-BUILD.md`。
> 初版日期：2026-09-17；SP02 于同日完成（方案 A 成功，未启用方案 B）。

## 1. 方案与结论

- **OpenSSL（D16）**：方案 A —— **vcpkg**（固定 ref `2026.07.29`，浅克隆于 `tools/vcpkg/`，不入库）。
  - UWP 三架构（arm/x86/x64）：overlay triplets `native/triplets/*-uwp-v141.cmake`
    （静态库 + 动态 CRT + `VCPKG_PLATFORM_TOOLSET=v141` + `VCPKG_VISUAL_STUDIO_PATH=VS2017`）。
    原因：VS2026 的 v142/v145 已移除 ARM32 目标，ARM32 只能由并存 VS2017 的 v141 提供（`doc/ENV.md`）。
  - 宿主机测试用：`x64-windows-static`（vcpkg 内置 triplet，VS2026 默认工具集）。
    注意：该 triplet 为**静态 CRT（/MT）**，宿主 GoogleTest 工程（X06）需同样 /MT 或换 `x64-windows-static-md`，届时在 X06/N01 定夺并回写此处。
  - 方案 B（vcpkg 失败时备用，未启用）：OpenSSL 源码 `perl Configure VC-WIN32-ARM-UWP no-shared no-tests no-apps no-docs no-engine no-legacy` + `nmake`；x86/x64 用 `VC-WIN32-ONECORE` / `VC-WIN64A-UWP`；3.x 不行再试 1.1.1w。
- **libssh2 1.11.1 / libvterm 0.3.3 / phc-winner-argon2 20190702（N01）**：源码直接编进 native 组件
  （vendored 到 `native/third_party/`，`scripts/fetch-third-party.ps1` 拉取校验）——SP03/N01 任务展开。

## 2. 一键命令

```pwsh
# OpenSSL 四个 triplet 全量（幂等可重跑；产物在 native/prebuilt/，不入库）
pwsh scripts/build-openssl.ps1

# 只构建某个：pwsh scripts/build-openssl.ps1 -Triplets arm-uwp-v141
```

## 3. 产物布局

```
native/prebuilt/
├── arm-uwp/              lib/libcrypto.lib (+libssl.lib)  include/openssl/*  VERSION.txt
├── x86-uwp/              同上
├── x64-uwp/              同上（SshTool.Native 按平台经 PrebuiltTriplet 属性引用）
└── x64-windows-static/   宿主机测试用（native/tests CMake 使用）
```

## 4. 实测记录（SP02，2026-09-17 回填）

| triplet | 结果 | 耗时 | 产物大小（libcrypto / libssl） | 备注 |
|---|---|---|---|---|
| x64-windows-static | ✓ | 7.4 min | 64.0 / 13.4 MB | VS2026 默认工具集，/MT |
| x86-uwp-v141 | ✓ | 3.9 min | 86.5 / 17.2 MB | 一次通过 |
| x64-uwp-v141 | ✓ | 3.4 min | 90.3 / 17.7 MB | 一次通过 |
| arm-uwp-v141 | ✓ | 3.9 min | 90.2 / 17.8 MB | 需 triplet 内补 /LIBPATH（踩坑 1/2） |

均为 OpenSSL **3.6.3**（vcpkg `2026.07.29` port）。x64 Debug / ARM Release 全链路（Native + sln + App 打包）通过。

踩坑：

1. **SDK ≥ 22621（本机 26100）已删除 `um/arm` 与 `ucrt/arm` 目录**，vcpkg 给链接探测注入的 `LIB` 取自最新 SDK → ARM 探测阶段 `LNK1104 找不到 WindowsApp.lib`（x86/x64 因 26100 仍有对应目录而"恰好"成功）。`VCPKG_CMAKE_SYSTEM_VERSION=10.0.19041.0` 只影响 CMake 侧 rc/mt 选择与 WINVER，**不改 vcpkg 注入的 LIB**；必须在 `arm-uwp-v141.cmake` 里用 `VCPKG_LINKER_FLAGS` 显式 `/LIBPATH` 补 19041 的 `um/arm` 与 `ucrt/arm`。
2. **`VCPKG_LINKER_FLAGS` 不能含带空格路径**：值经 vcpkg → CMake `-D` 传递时内嵌引号被剥掉，`C:/Program Files ...` 被截断成 `/LIBPATH:C:/Program`。必须用 **8.3 短路径**。8.3 名按机器生成，本机为 `C:\PROGRA~2\WI3CF2~1 = C:\Program Files (x86)\Windows Kits`、`100190~1.0 = 10.0.19041.0`；换机用 `dir /x` 或 `(New-Object -ComObject Scripting.FileSystemObject).GetFolder(<路径>).ShortPath` 重查并改 triplet。
3. Native 组件链接 `libcrypto.lib` 需同时加 **`crypt32.lib`**（OpenSSL winstore 证书存储代码引用 `CertOpenSystemStoreW`），否则 `LNK2019 __imp_CertOpenSystemStoreW`。
4. vcxproj 的 XML 文本节点里路径不得断行（手滑在 `..\..\native\...` 中引入换行会让 include/lib 目录碎成两段，cl 随后把 /AI 的带空格 SDK 路径当源文件，报一堆 D9024/D9027 警告）。
