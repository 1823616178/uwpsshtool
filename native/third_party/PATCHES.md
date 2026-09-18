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

## libvterm 0.3.3（N01 引入）

- 抓取：`https://www.leonerd.org.uk/code/libvterm/libvterm-0.3.3.tar.gz`
  SHA256 `09156F43DD2128BD347CBEEBE50D9A571D32C64E0CF18D211197946AFF7226E0`
- **源码补丁零处。** 使用官方 release tar，不使用 GitHub 源码快照；包内已包含
  `src/fullwidth.inc`、`src/encoding/uk.inc`、`src/encoding/DECdrawing.inc`，无需 Perl 生成。
- v141 实测可编译其 C99 指定初始化器。工程侧定义 `_CRT_SECURE_NO_WARNINGS`，并关闭
  `parser.c` 控制流引发的 C4703 误报；这两项均为编译选项，不修改上游文件。
- `libvterm/src/encoding.c` 与 Argon2 的同名文件使用独立对象目录，防止 MSBuild 覆盖 `.obj`。

## phc-winner-argon2 20190702（N01 引入）

- 抓取：`https://github.com/P-H-C/phc-winner-argon2/archive/refs/tags/20190702.tar.gz`
  SHA256 `DAF972A89577F8772602BF2EB38B6A3DD3D922BF5724D45E7F9589B5E830442C`
- **源码补丁零处。** 只编译 `argon2.c`、`core.c`、`encoding.c`、`ref.c`、`thread.c`、
  `blake2/blake2b.c`；不编 `opt.c`，并定义 `ARGON2_NO_THREADS`。`thread.c` 在该宏下为空实现，
  保留它是为了让清单与上游 reference build 结构一致。
- 工程侧定义 `_CRT_SECURE_NO_WARNINGS` 以允许上游编码函数的 `sprintf`；不改算法源码。
- RFC 9106 §5.3 的 Argon2id 官方向量由宿主测试逐字节验证。
