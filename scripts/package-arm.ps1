# scripts/package-arm.ps1 - 打包、签名与准备侧载分发目录
# 用于构建 ARM Release 包（或 x86/x64），并组织自签名证书与框架依赖包。
#
# 用法:
#   pwsh scripts/package-arm.ps1                       # 构建 ARM Release 并组织分发目录
#   pwsh scripts/package-arm.ps1 -Sign                 # 构建并用自签名证书为包签名，导出 .cer
#   pwsh scripts/package-arm.ps1 -SkipBuild -Sign      # 仅对已构建产物签名并组织分发
#   pwsh scripts/package-arm.ps1 -Platform x64         # 构建 x64 Debug/Release 包
[CmdletBinding()]
param(
    [ValidateSet('ARM', 'x64', 'x86', 'All')]
    [string]$Platform = 'ARM',
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [string]$OutputDir,
    [switch]$Sign,
    [switch]$SkipBuild,
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

function Find-MsBuild([string]$versionRange) {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere)) { throw "未找到 vswhere: $vswhere" }
    $path = & $vswhere -version $versionRange -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
    if (-not $path) { throw "未找到 MSBuild (版本区间 $versionRange)" }
    return $path
}

function Find-SignTool {
    $kitDirs = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.19041.0\x64\signtool.exe",
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe",
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.16299.0\x64\signtool.exe"
    )
    foreach ($k in $kitDirs) {
        if (Test-Path $k) { return $k }
    }
    $found = Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match 'x64\\signtool\.exe$' } | Select-Object -First 1
    if ($found) { return $found.FullName }
    return $null
}

# 读取当前包版本号
$manifestPath = Join-Path $RepoRoot 'src\SshTool.App\Package.appxmanifest'
[xml]$doc = Get-Content $manifestPath -Raw
$version = $doc.Package.Identity.Version
$publisher = $doc.Package.Identity.Publisher
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host " Lumia SSH 打包与发布工具" -ForegroundColor Cyan
Write-Host " 包版本: $version | 发布者: $publisher" -ForegroundColor Cyan
Write-Host " 目标架构: $Platform | 构建配置: $Configuration" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

# 1. 构建
if (-not $SkipBuild) {
    $msbuildNative = Find-MsBuild '[15.0,16.0)'
    $msbuildSln = Find-MsBuild '[17.0,)'

    $platformsToBuild = if ($Platform -eq 'All') { @('ARM', 'x64') } else { @($Platform) }

    foreach ($plat in $platformsToBuild) {
        $nativePlat = if ($plat -eq 'x86') { 'Win32' } else { $plat }
        Write-Host "`n[1/3] 构建 Native ($nativePlat $Configuration)..." -ForegroundColor Green
        if ($Clean) {
            & $msbuildNative src/SshTool.Native/SshTool.Native.vcxproj -t:Clean -p:Configuration=$Configuration -p:Platform=$nativePlat -v:m -nologo -nr:false
        }
        & $msbuildNative src/SshTool.Native/SshTool.Native.vcxproj -p:Configuration=$Configuration -p:Platform=$nativePlat -v:m -nologo -nr:false
        if ($LASTEXITCODE -ne 0) { throw "Native 构建失败 ($nativePlat $Configuration)" }

        Write-Host "`n[2/3] 构建 App 与打包 ($plat $Configuration)..." -ForegroundColor Green
        if ($Clean) {
            & $msbuildSln SshTool.sln -t:Clean -p:Configuration=$Configuration -p:Platform=$plat -v:m -nologo -nr:false
        }
        & $msbuildSln SshTool.sln -p:Configuration=$Configuration -p:Platform=$plat -v:m -nologo -nr:false
        if ($LASTEXITCODE -ne 0) { throw "App 构建与打包失败 ($plat $Configuration)" }
    }
} else {
    Write-Host "`n[跳过构建阶段 (-SkipBuild)]" -ForegroundColor Yellow
}

# 2. 定位生成的 AppX 包
$appxDir = Join-Path $RepoRoot "src\SshTool.App\AppPackages\SshTool.App_${version}_${Platform}_Test"
if (-not (Test-Path $appxDir)) {
    # 尝试查找匹配版本和架构的最新目录
    $candidate = Get-ChildItem (Join-Path $RepoRoot 'src\SshTool.App\AppPackages') -Directory |
        Where-Object { $_.Name -like "*_${Platform}_Test" -or $_.Name -like "*_${Platform}_${Configuration}_Test" } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($candidate) {
        $appxDir = $candidate.FullName
    } else {
        throw "未找到匹配的包目录: src\SshTool.App\AppPackages\*_${Platform}_Test"
    }
}

$appxFile = Get-ChildItem $appxDir -Filter "*.appx" | Where-Object { $_.Name -notlike "*Dependencies*" } | Select-Object -First 1
if (-not $appxFile) { throw "在 $appxDir 中未找到 .appx 包文件" }
Write-Host "已定位包文件: $($appxFile.FullName)" -ForegroundColor Green

# 3. 组织发布输出目录
if (-not $OutputDir) {
    $OutputDir = Join-Path $RepoRoot "artifacts\release\SshTool_${version}_${Platform}"
}
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$destAppx = Join-Path $OutputDir $appxFile.Name
Copy-Item $appxFile.FullName $destAppx -Force
Write-Host "已复制主包至: $destAppx"

# 复制框架依赖包
$depSourceDir = Join-Path $appxDir "Dependencies\$($Platform.ToLower())"
$destDepDir = Join-Path $OutputDir "Dependencies\$($Platform.ToLower())"
if (Test-Path $depSourceDir) {
    if (-not (Test-Path $destDepDir)) { New-Item -ItemType Directory -Path $destDepDir -Force | Out-Null }
    Copy-Item "$depSourceDir\*.appx" $destDepDir -Force
    $depCount = (Get-ChildItem $destDepDir -Filter *.appx).Count
    Write-Host "已复制 $depCount 个框架依赖包至: $destDepDir" -ForegroundColor Green
} else {
    Write-Warning "未找到依赖目录: $depSourceDir"
}

# 4. 签名支持 (-Sign)
$certDir = Join-Path $RepoRoot 'artifacts\certs'
$pfxPath = Join-Path $certDir 'LumiaSshDev.pfx'
$cerPath = Join-Path $certDir 'LumiaSshDev.cer'
$pfxPassword = 'LumiaSsh2026'

if ($Sign) {
    Write-Host "`n[3/3] 签名处理..." -ForegroundColor Green
    $signtool = Find-SignTool
    if (-not $signtool) {
        Write-Warning "未找到 signtool.exe，跳过二进制签名"
    } else {
        if (-not (Test-Path $pfxPath)) {
            Write-Host "正在生成自签名证书 (Subject: $publisher)..."
            if (-not (Test-Path $certDir)) { New-Item -ItemType Directory -Path $certDir -Force | Out-Null }
            $cert = New-SelfSignedCertificate -Type Custom `
                -Subject $publisher `
                -KeyUsage DigitalSignature `
                -FriendlyName "Lumia SSH Developer Certificate" `
                -CertStoreLocation "Cert:\CurrentUser\My" `
                -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3") `
                -NotAfter (Get-Date).AddYears(5)

            $securePwd = ConvertTo-SecureString $pfxPassword -AsPlainText -Force
            Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $securePwd | Out-Null
            Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null
            Write-Host "自签名证书已生成: $pfxPath" -ForegroundColor Green
        }

        Write-Host "使用 $signtool 签名包文件..."
        & $signtool sign /fd SHA256 /a /f $pfxPath /p $pfxPassword $destAppx
        if ($LASTEXITCODE -ne 0) {
            throw "signtool 签名失败（退出码 $LASTEXITCODE）"
        }
        Write-Host "签名完成 ✓" -ForegroundColor Green

        # 导出公钥证书至发布包目录供手机信任导入
        Copy-Item $cerPath (Join-Path $OutputDir 'LumiaSshDev.cer') -Force
        Write-Host "已导出受信任根证书至: $(Join-Path $OutputDir 'LumiaSshDev.cer')"
    }
}

# 5. 生成安装说明文件
$readmeContent = @"
=====================================================
 Lumia SSH (v$version $Platform) 旁加载安装说明
=====================================================

1. 手机准备：
   - 打开 Lumia「设置」→「更新和安全」→「针对开发人员」
   - 勾选「开发人员模式」
   - 开启「设备发现」并生成配对 PIN（如使用 USB 部署）

2. 一键脚本安装（推荐）：
   在电脑端打开 PowerShell 并运行本工程脚本：
   pwsh scripts/deploy-phone.ps1 -Ip 127.0.0.1 -Pin <手机PIN> -Package "$destAppx"

3. 设备门户网页安装：
   - 打开浏览器访问 https://127.0.0.1:10443 或手机局域网 IP:10443
   - 进入 Apps 页面，在「Deploy apps」上传:
     主包: $(Split-Path $destAppx -Leaf)
     依赖项 (Dependencies\$($Platform.ToLower())):
$(if (Test-Path $destDepDir) { (Get-ChildItem $destDepDir -Filter *.appx | ForEach-Object { "       - $($_.Name)" }) -join "`n" })
   - 点击 Deploy 完成安装

4. 证书信任（若需）：
   - 将 LumiaSshDev.cer 传至手机存储中点击安装，选择安装至「受信任的根证书颁发机构」
"@
$readmeContent | Set-Content (Join-Path $OutputDir 'README-INSTALL.txt') -Encoding UTF8

Write-Host "`n==========================================" -ForegroundColor Cyan
Write-Host " 打包完成！发布目录如下:" -ForegroundColor Cyan
Write-Host " $OutputDir" -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Cyan
Get-ChildItem $OutputDir -Recurse | Select-Object FullName, Length | Format-Table -AutoSize
