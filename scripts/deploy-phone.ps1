# scripts/deploy-phone.ps1 - Lumia 真机一键部署工具
# 支持 WinAppDeployCmd 与 Windows Device Portal 两种部署模式。
#
# 用法:
#   pwsh scripts/deploy-phone.ps1 -Ip 127.0.0.1 -Pin 3XKF9Q       # USB 隧道一键安装最新包
#   pwsh scripts/deploy-phone.ps1 -Ip 192.168.1.100 -Pin 3XKF9Q   # 局域网 Wi-Fi 安装
#   pwsh scripts/deploy-phone.ps1 -Package <路径> -Pin 3XKF9Q     # 指定特定包安装
#   pwsh scripts/deploy-phone.ps1 -UsePortal                      # 使用 Device Portal 网页 REST API 部署
#   pwsh scripts/deploy-phone.ps1 -Uninstall                      # 卸载旧应用
[CmdletBinding()]
param(
    [string]$Ip = '127.0.0.1',
    [string]$Pin,
    [string]$Package,
    [switch]$UsePortal,
    [switch]$UseDeployCmd,
    [switch]$Uninstall,
    [string]$PackageFamily = 'SshTool.LumiaSsh_xxxx' # 实际由 appxmanifest 包名匹配
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot

function Find-WinAppDeployCmd {
    $kitDirs = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.19041.0\x86\WinAppDeployCmd.exe",
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.26100.0\x86\WinAppDeployCmd.exe",
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.16299.0\x86\WinAppDeployCmd.exe"
    )
    foreach ($k in $kitDirs) {
        if (Test-Path $k) { return $k }
    }
    $found = Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Filter WinAppDeployCmd.exe -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($found) { return $found.FullName }
    return $null
}

# 1. 解析目标安装包
$packageFile = $null
if ($Package) {
    if (-not (Test-Path $Package)) { throw "指定的包路径不存在: $Package" }
    $packageFile = Get-Item $Package
} else {
    # 自动定位最新的 ARM Release 包
    $candidates = @()
    $releaseDir = Join-Path $RepoRoot 'artifacts\release'
    if (Test-Path $releaseDir) {
        $candidates += Get-ChildItem $releaseDir -Filter "*.appx" -Recurse | Where-Object { $_.FullName -notmatch 'Dependencies' }
    }
    $appPackagesDir = Join-Path $RepoRoot 'src\SshTool.App\AppPackages'
    if (Test-Path $appPackagesDir) {
        $candidates += Get-ChildItem $appPackagesDir -Filter "*.appx" -Recurse | Where-Object { $_.FullName -notmatch 'Dependencies' -and $_.FullName -match 'ARM' }
    }

    if ($candidates.Count -eq 0) {
        throw "未找到可部署的 .appx 包，请先运行: pwsh scripts/package-arm.ps1"
    }

    $packageFile = $candidates | Sort-Object LastWriteTime -Descending | Select-Object -First 1
}

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host " Lumia SSH 真机部署工具" -ForegroundColor Cyan
Write-Host " 目标设备 IP: $Ip" -ForegroundColor Cyan
Write-Host " 部署包: $($packageFile.FullName)" -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Cyan

# 2. 解析框架依赖包
$pkgDir = $packageFile.Directory.FullName
$depDir = Join-Path $pkgDir 'Dependencies\arm'
if (-not (Test-Path $depDir)) {
    # 尝试在同级或父级搜索 Dependencies\arm
    $parentDep = Join-Path (Split-Path $pkgDir -Parent) 'Dependencies\arm'
    if (Test-Path $parentDep) { $depDir = $parentDep }
}

$dependencies = @()
if (Test-Path $depDir) {
    $dependencies = Get-ChildItem $depDir -Filter "*.appx"
    Write-Host "发现 $($dependencies.Count) 个框架依赖包:" -ForegroundColor Gray
    foreach ($d in $dependencies) {
        Write-Host "  - $($d.Name)" -ForegroundColor Gray
    }
} else {
    Write-Warning "未找到 Dependencies\arm 依赖目录，若手机未安装运行库可能报错 0x80080204"
}

# 3. 卸载旧应用（若指定）
$deployCmd = Find-WinAppDeployCmd

if ($Uninstall) {
    if ($deployCmd) {
        Write-Host "`n执行卸载..." -ForegroundColor Yellow
        $unArgs = @('uninstall', '-package', 'SshTool.LumiaSsh', '-ip', $Ip)
        if ($Pin) { $unArgs += @('-pin', $Pin) }
        & $deployCmd @unArgs
    }
}

# 4. 部署执行
$deploySuccess = $false

if ($deployCmd -and -not $UsePortal) {
    Write-Host "`n[模式: WinAppDeployCmd 部署]" -ForegroundColor Green
    $cmdArgs = @('install', '-file', $packageFile.FullName, '-ip', $Ip)
    if ($Pin) {
        $cmdArgs += @('-pin', $Pin)
    }
    foreach ($dep in $dependencies) {
        $cmdArgs += @('-dependency', $dep.FullName)
    }

    Write-Host "正在向 $Ip 发起部署（可能需要 30~60 秒）..."
    & $deployCmd @cmdArgs
    if ($LASTEXITCODE -eq 0) {
        $deploySuccess = $true
        Write-Host "WinAppDeployCmd 部署成功 ✓" -ForegroundColor Green
    } else {
        Write-Warning "WinAppDeployCmd 部署返回退出码 $LASTEXITCODE，尝试使用 Device Portal REST 模式回退..."
    }
}

if (-not $deploySuccess) {
    Write-Host "`n[模式: Windows Device Portal REST 部署]" -ForegroundColor Green
    $portalScript = Join-Path $RepoRoot 'scripts\phone-portal.ps1'
    if (-not (Test-Path $portalScript)) {
        throw "未找到 phone-portal.ps1 脚本: $portalScript"
    }

    $portalArgs = @('-Install', $packageFile.FullName)
    if ($Pin) {
        Write-Host "首次配对 Device Portal (PIN: $Pin)..."
        & pwsh -NoProfile -File $portalScript -Pair $Pin -BaseUrl "https://${Ip}:10443"
    }

    Write-Host "调用 phone-portal.ps1 上传包与依赖..."
    & pwsh -NoProfile -File $portalScript -Install $packageFile.FullName -BaseUrl "https://${Ip}:10443"
    if ($LASTEXITCODE -eq 0) {
        $deploySuccess = $true
        Write-Host "Device Portal 部署完成 ✓" -ForegroundColor Green
    } else {
        throw "Device Portal 部署失败（退出码 $LASTEXITCODE）"
    }
}

Write-Host "`n==========================================" -ForegroundColor Cyan
Write-Host " 安装成功！您可以在手机「应用列表」中找到「Lumia SSH」启动" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
exit 0
