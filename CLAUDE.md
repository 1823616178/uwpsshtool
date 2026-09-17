# CLAUDE.md

Lumia SSH（uwpsshtool）：在 Lumia 950（Windows 10 Mobile，ARM32）上运行的原生 SSH 终端客户端（UWP，C# + C++/CX），与桌面端 `E:\code\ssh-tool`、鸿蒙端共用同一账号 / 保险库 / 云端同步文档。

**开工第一件事：读 `doc/README.md` 的「AI 分次编码工作流」，再从 `doc/04-TASKS.md` 选下一个任务。每次只做一个任务。**

## 硬性约束（摘要，全文见 doc/README.md）

- 不改外部仓库：`E:\code\ssh-tool`、`G:\code\ssh-tool-server`、`C:\Users\lx182\DevEcoStudioProjects\ssh_client_ohos` 只读。
- 同步格式零偏差：云端文档严格等于桌面端 `SyncDocumentV1`（权威：`E:\code\ssh-tool\src\shared\sync-schemas.ts`）。
- 本机专有数据不上云、下行不得清空。
- XAML/C# 禁止魔法数字与硬编码颜色，一律引用 `Themes/Tokens.xaml`。
- 日志脱敏：不记录密码、私钥、token、同步密码、恢复密钥、同步明文/密文。
- W10M 兼容：`TargetPlatformMinVersion = 10.0.15063.0`；高于 15063 的 API 必须 `ApiInformation` 守卫；C# 语言版本 7.3；共享库只到 `netstandard1.4`。
- 构建工具链（X01 实测，详见 `doc/ENV.md`）：C# 用 VS2026 MSBuild；Native C++ 用 v141 工具集（VS2026 的 v142/v145 无 ARM32）；SDK Target 10.0.19041（勿升 ≥22621）。

## 构建 / 测试命令

```pwsh
dotnet test tests/SshTool.Core.Tests            # Core 纯逻辑测试（宿主机 net8；Tests 不入 sln）
pwsh scripts/verify.ps1                         # 一键门禁（X06 起；之前按任务「验证」栏执行）

# 全量构建是两段式（原因见 doc/ENV.md：VS2026 无 ARM32/v141 UWP 工具，Native 须由 VS2017 v141 构建）：
$vs2026 = "C:\Program Files\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\MSBuild.exe"
$vs2017 = "C:\Program Files (x86)\Microsoft Visual Studio\2017\Community\MSBuild\15.0\Bin\MSBuild.exe"
& $vs2026 SshTool.sln -t:Restore
# 1) Native（x64 Debug 示例；真机用 -p:Platform=ARM -p:Configuration=Release）
& $vs2017 src/SshTool.Native/SshTool.Native.vcxproj -p:Configuration=Debug -p:Platform=x64
# 2) C# 全部（SshTool.sln 中 Native 不参与构建，App 直接引用其 winmd 产物）
& $vs2026 SshTool.sln -p:Configuration=Debug -p:Platform=x64
& $vs2026 SshTool.sln -p:Configuration=Release -p:Platform=ARM   # .NET Native，耗时
```
> 若日后安装 VS2019 16.11（含 C++ v142 UWP 工具），可恢复单命令解决方案构建。
