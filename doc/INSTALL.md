# Lumia SSH 侧载与安装指南（doc/INSTALL.md）

> 本文档为 **Q09 打包、签名与侧载指南** 的标准化交付产物。
> 详细指导如何在 Windows 10 Mobile（如 Lumia 950 / 950XL / 650 / 640 等设备，系统版本 10.0.15063+）
> 从零安装、信任证书、通过 USB / Wi-Fi 部署，以及无损升级应用的完整流程。

---

## 1. 适用环境与准备工作

### 1.1 手机端系统要求
- **系统版本**：Windows 10 Mobile（创意者更新 10.0.15063 或更高版本）。
- **设备架构**：ARM32（如 Lumia 950/950XL 运行的 32 位系统）。
- **可用空间**：手机内部存储建议预留至少 300 MB 可用空间。

### 1.2 开启手机开发人员模式
1. 在手机上进入「设置」→「更新和安全」→「针对开发人员」。
2. 在「使用开发人员功能」下勾选 **「开发人员模式」**（若弹出提示框，点击确认）。
3. 向下滚动开启 **「设备发现」**（Device Discovery）：
   - 点击「配对」（Pair）即可生成一个 6 位大小写不敏感的 PIN 码（每次配对有效，5 分钟超时）。
4. 开启 **「设备门户」**（Device Portal）：
   - 勾选「打开局域网诊断」（Enable diagnostics over local area network connections）。
   - 勾选「通过 USB 连接提供诊断」或记录手机在 Wi-Fi 局域网中的 IP 地址。

---

## 2. 安装包获取与目录结构

使用工程打包脚本可一键产出标准的发布目录：
```powershell
pwsh scripts/package-arm.ps1 -Sign
```
打包完成后产物位于 `artifacts/release/SshTool_<版本>_ARM/`，包含以下文件：
```
SshTool_<版本>_ARM/
├── SshTool.App_<版本>_ARM.appx       # 应用主程序包（已签名）
├── LumiaSshDev.cer                  # 测试根证书（公共证书）
├── README-INSTALL.txt               # 便捷说明文档
└── Dependencies/
    └── arm/
        ├── Microsoft.NET.Native.Framework.1.7.appx  # .NET Native 框架运行库
        ├── Microsoft.NET.Native.Runtime.1.7.appx    # .NET Native 运行时
        └── Microsoft.VCLibs.ARM.14.00.appx          # Visual C++ UWP 运行库
```

---

## 3. 根证书安装与信任（首次安装必须）

UWP 侧载包（Sideloading）在非应用商店渠道分发时，系统必须信任该包的签名证书。

### 3.1 手动导入证书至手机
1. 将 `LumiaSshDev.cer` 通过 USB 数据线、OneDrive 或局域网发送至手机（如存入手机「文档」或「下载」目录）。
2. 在手机内置的「文件资源管理器」中定位到 `LumiaSshDev.cer` 并点击打开。
3. 系统将弹出证书安装向导：
   - 证书存储区选择 **「受信任的根证书颁发机构」**（Trusted Root Certification Authorities）。
   - 点击「安装」并确认导入。
4. 导入完成后，手机将允许直接安装所有带有 `CN=LumiaSshDev` 签名的应用包。

> **提示**：如果使用已配对的 Windows Device Portal 网页上传或 `WinAppDeployCmd -pin` 方式部署，
> 部署通道会自动为已配对的开发机信任签名，但在手机独立运行时依然建议导入证书。

---

## 4. 部署方法

### 方法 A：一键自动化脚本部署（推荐）

本工程提供了自动化部署脚本 `scripts/deploy-phone.ps1`，能自动解析主包与 3 个依赖包并按序推送到手机：

1. **USB 连接（最快最稳）**：
   手机用 USB 线连接电脑，在手机开发人员页面点击「配对」生成 PIN 码后运行：
   ```powershell
   pwsh scripts/deploy-phone.ps1 -Ip 127.0.0.1 -Pin <6位PIN>
   ```
2. **Wi-Fi 无线部署**：
   手机与电脑处于同一局域网下（如电脑热点），查看手机 IP（如 `192.168.1.105`）：
   ```powershell
   pwsh scripts/deploy-phone.ps1 -Ip 192.168.1.105 -Pin <6位PIN>
   ```

### 方法 B：通过 Windows Device Portal 网页部署

适合无需安装 Windows SDK 命令行工具的用户：
1. 确保手机已开启「设备门户」。
2. 在电脑浏览器打开 `https://127.0.0.1:10443`（USB 模式）或 `https://<手机IP>:10443`（Wi-Fi 模式）。
   - 首次访问如报安全证书警告，选择「继续访问（不安全）」。
   - 如要求输入 PIN，在手机开发人员设置点击「配对」填入。
3. 导航到左侧菜单 **「Apps」** 页面。
4. 在 **「Deploy apps」** 区域：
   - **App package**：点击「Browse」选中 `SshTool.App_<版本>_ARM.appx`。
   - **Dependency**：点击「Add dependency」分别选择 `Dependencies\arm\` 下的 3 个 `.appx` 依赖包：
     - `Microsoft.NET.Native.Framework.1.7.appx`
     - `Microsoft.NET.Native.Runtime.1.7.appx`
     - `Microsoft.VCLibs.ARM.14.00.appx`
5. 点击 **「Deploy」** 按钮，等待进度条走完至提示 `Installation completed`。

### 方法 C：WinAppDeployCmd 命令行手动部署

若需手工调用 Windows 10 SDK 命令行工具：
```powershell
$deployCmd = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.19041.0\x86\WinAppDeployCmd.exe"
& $deployCmd install `
    -file "artifacts\release\SshTool_0.1.0.10_ARM\SshTool.App_0.1.0.10_ARM.appx" `
    -dependency "artifacts\release\SshTool_0.1.0.10_ARM\Dependencies\arm\Microsoft.NET.Native.Framework.1.7.appx" `
    -dependency "artifacts\release\SshTool_0.1.0.10_ARM\Dependencies\arm\Microsoft.NET.Native.Runtime.1.7.appx" `
    -dependency "artifacts\release\SshTool_0.1.0.10_ARM\Dependencies\arm\Microsoft.VCLibs.ARM.14.00.appx" `
    -ip 127.0.0.1 `
    -pin <手机PIN>
```

---

## 5. 升级安装与数据安全（重要）

### 5.1 原地升级机制
- **同包覆盖安装**：只要后续版本的应用包保持相同的 `PackageIdentity`（`Name="SshTool.LumiaSsh" Publisher="CN=LumiaSshDev"`）且版本号严格递增（如 `0.1.0.10` → `0.1.0.11`），系统会直接执行原地更新（In-place Update）。
- **递增版本号**：在构建新包前使用 `pwsh scripts/bump-version.ps1` 增加构建版本号。

### 5.2 数据保留保证
UWP 应用程序的持久化数据均保存在其 AppContainer 沙盒中：
- `LocalFolder/secure/secrets.bin`：由系统 DPAPI 加密的全部主机密码、私钥密码与保险库密钥；
- `LocalFolder/hosts.json`：全部已保存主机、分组与隧道配置；
- `LocalFolder/known_hosts.json`：已信任的主机公钥指纹与 TOFU 记录；
- `LocalFolder/sync_vault.json`：端到端加密同步保险库本地缓存；
- `LocalFolder/logs/app.log`：诊断与运行日志。

> ⚠️ **严禁先卸载后安装**：
> 覆盖升级会自动且完整保留上述全部数据！
> 若在手机上长按应用选择「卸载」，Windows 系统会自动清空并销毁整个沙盒目录（包括 DPAPI 保护的全部私密凭据），
> 导致所有配置彻底丢失！升级时请直接安装新包，无需卸载旧版。

---

## 6. 常见错误代码与排查手册

| 错误代码 / 现象 | 根因解释 | 处置方法 |
|---|---|---|
| **0x80080204** | 应用依赖的框架包缺失或架构不匹配 | 必须在安装主包时同时上传 `Dependencies\arm\` 下的全部 3 个包，缺一不可。 |
| **0x80073CF0** / **0x80073CF9** | 证书不受信任或包已损坏 | 确认已将 `LumiaSshDev.cer` 安装至「受信任的根证书颁发机构」，且包未被修改。 |
| **0x8007007E** | 运行时找不到 Native 依赖库 | 缺少 Visual C++ 运行时，确认手机安装了 `Microsoft.VCLibs.ARM.14.00.appx`。 |
| **0x80073CF3** | 同版本包已存在，无法重复安装 | 运行 `pwsh scripts/bump-version.ps1` 递增版本号后重新打包安装。 |
| **PIN 错误 / 401 Unauthorized** | 手机端生成的配对 PIN 码已过期（5分钟） | 在手机开发人员设置界面再次点击「配对」生成全新 6 位 PIN 码并重试。 |
| **Device Portal 无法连接** | USB 端口转发未建立或 Wi-Fi 隔离 | 检查手机是否连接良好，确认手机锁屏已解锁；Wi-Fi 需在同一子网。 |
| **安装过程中断 / 存储满** | 手机内部存储空间不足以解包 .NET Native | 清理手机存储（删除无用视频/缓存），确保预留至少 300 MB 空间。 |
