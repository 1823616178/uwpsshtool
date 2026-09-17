# 第三方源码补丁记录

> 源码由 `scripts/fetch-third-party.ps1` 按固定 URL + SHA256 抓取，**不入库**（见 `.gitignore`）。
> 本文件记录我们对第三方源码做的每一处改动，以及"为什么不用改"的结论——后者同样重要，
> 换机或升级版本时可据此判断是否需要重新处理。

## libssh2 1.11.1（SP03 引入）

- 抓取：`https://github.com/libssh2/libssh2/releases/download/libssh2-1.11.1/libssh2-1.11.1.tar.gz`
  SHA256 `D9EC76CBE34DB98EEC3539FE2C899D26B0C837CB3EB466A56B0F109CABF658F7`

### 源码补丁

**零处。** 1.11.1 在 ARM32 UWP（v141 工具集、AppContainer、`/ZW` 工程内）下**未经修改即可编译链接通过**
（x64 Debug 与 ARM Release 均已实测）。

### 不用改的原因（逐条结论）

| 事项 | 结论 |
|---|---|
| `libssh2_config.h` | **不需要**。1.11 的 `src/libssh2_setup.h` 自带 `_WIN32` 分支，已定义 `HAVE_SELECT`/`HAVE_SNPRINTF` 等 |
| 加密后端 | 编译期定义 `LIBSSH2_OPENSSL`，链接 SP02 产出的 `libcrypto.lib`（`native/prebuilt/<triplet>/lib`） |
| `agent_win.c` | **排除编译**。它走 Pageant 窗口消息（`FindWindow`/`SendMessage`）与命名管道，UWP 禁用；本项目也不需要 ssh-agent 转发（K 系列如需再议） |
| `os400qc3.c` | 排除编译（OS/400 专用后端） |
| 其余后端 `libgcrypt.c` / `mbedtls.c` / `wincng.c` | 保留在编译列表里，靠自身 `#ifdef` 全部编译为空 |
| Winsock | AppContainer 下链接无禁用 API 报错；`socket`/`connect`/`select`/`getaddrinfo` 均可用（真机连通性由 SP03 📱 验收确认） |

### 工程侧接线（`src/SshTool.Native/SshTool.Native.vcxproj`）

- `$(Libssh2Dir)src\*.c` 以 `CompileAs=CompileAsC`、`PrecompiledHeader=NotUsing`、`CompileAsWinRT=false` 编译
  （C 文件不能用 C++/CX 的预编译头，也不能开 `/ZW`）。
- 全工程加 `/utf-8`：不加时 MSVC 按 GBK 读 UTF-8 源码，中文注释的尾字节会吞掉下一行，
  报出「找不到标识符」这类完全对不上的错（`doc/ENV.md` §5，SP03 又踩了一次）。
- 源码缺失时由 `RequireThirdParty` 目标给出可操作报错，而不是满屏"找不到 libssh2.h"。
