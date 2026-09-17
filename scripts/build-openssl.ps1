# SP02：构建 OpenSSL 静态库（UWP 三架构 + 宿主机）→ native/prebuilt/<triplet>/
#
# 方案 A：vcpkg（固定 ref，见 $VcpkgRef）。UWP 三架构走 overlay triplets（native/triplets/*-uwp-v141）：
# ARM32 只能由 VS2017 v141 提供（VS2026 v142/v145 无 ARM32，见 doc/ENV.md）。
# 方案 B（vcpkg 失败时，见 native/NATIVE-BUILD.md）：OpenSSL 源码 perl Configure VC-WIN32-ARM-UWP + nmake。
#
# 用法：
#   pwsh scripts/build-openssl.ps1                      # 全部四个 triplet
#   pwsh scripts/build-openssl.ps1 -Triplets arm-uwp-v141
# 幂等可重跑；产物不入库（.gitignore native/prebuilt/），脚本是唯一事实来源。
[CmdletBinding()]
param(
    # 逗号分隔的单个字符串（兼容 bash/cmd 各种调用方式）；空 = 全部
    [string]$Triplets = ""
)

$ErrorActionPreference = 'Stop'
$RepoRoot     = Split-Path -Parent $PSScriptRoot
$VcpkgRef     = '2026.07.29'
$VcpkgDir     = Join-Path $RepoRoot 'tools\vcpkg'
$OverlayDir   = Join-Path $RepoRoot 'native\triplets'
$PrebuiltRoot = Join-Path $RepoRoot 'native\prebuilt'

# doc/04-TASKS SP02 产出目录名 ← vcpkg triplet 名
$PrebuiltNames = [ordered]@{
    'x64-windows-static' = 'x64-windows-static'
    'x86-uwp-v141'       = 'x86-uwp'
    'x64-uwp-v141'       = 'x64-uwp'
    'arm-uwp-v141'       = 'arm-uwp'
}

function Ensure-Vcpkg {
    if (-not (Test-Path (Join-Path $VcpkgDir '.git'))) {
        Write-Host "== 克隆 vcpkg ($VcpkgRef) 到 tools/vcpkg =="
        git clone --depth 1 --branch $VcpkgRef https://github.com/microsoft/vcpkg.git $VcpkgDir
        if ($LASTEXITCODE -ne 0) { throw "git clone vcpkg 失败" }
    }
    if (-not (Test-Path (Join-Path $VcpkgDir 'vcpkg.exe'))) {
        Write-Host "== bootstrap vcpkg =="
        Push-Location $VcpkgDir
        & cmd /c "bootstrap-vcpkg.bat -disableMetrics"
        Pop-Location
        if ($LASTEXITCODE -ne 0) { throw "bootstrap vcpkg 失败" }
    }
}

Ensure-Vcpkg
$vcpkg = Join-Path $VcpkgDir 'vcpkg.exe'
$opensslVersion = (Get-Content (Join-Path $VcpkgDir 'ports\openssl\vcpkg.json') -Raw | ConvertFrom-Json).version
Write-Host "vcpkg ref: $VcpkgRef；openssl port 版本: $opensslVersion"

$tripletList = @()
if ([string]::IsNullOrWhiteSpace($Triplets)) {
    $tripletList = @($PrebuiltNames.Keys)
} else {
    $tripletList = @($Triplets -split ',' | ForEach-Object { $_.Trim(" ", "'") } | Where-Object { $_ -ne '' })
}

$failed = @()
foreach ($t in $tripletList) {
    if (-not $PrebuiltNames.Contains($t)) { throw "未知 triplet: $t（可选：$($PrebuiltNames.Keys -join ', ')）" }
    $dest = Join-Path $PrebuiltRoot $PrebuiltNames[$t]
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Write-Host "`n== openssl:$t → native/prebuilt/$($PrebuiltNames[$t]) =="
    try {
        & $vcpkg install "openssl:$t" --overlay-triplets="$OverlayDir" --clean-after-build
        if ($LASTEXITCODE -ne 0) { throw "vcpkg install 退出码 $LASTEXITCODE" }

        $installed = Join-Path $VcpkgDir "installed\$t"
        New-Item -ItemType Directory -Force "$dest\lib", "$dest\include" | Out-Null
        Copy-Item (Join-Path $installed 'lib\libcrypto.lib') "$dest\lib\" -Force
        if (Test-Path (Join-Path $installed 'lib\libssl.lib')) {
            Copy-Item (Join-Path $installed 'lib\libssl.lib') "$dest\lib\" -Force
        }
        Copy-Item (Join-Path $installed 'include\openssl') "$dest\include\" -Recurse -Force

        $sw.Stop()
        @(
            "triplet: $t"
            "openssl: $opensslVersion"
            "vcpkg: $VcpkgRef"
            "built: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
            "elapsed_min: $([math]::Round($sw.Elapsed.TotalMinutes, 1))"
        ) | Set-Content (Join-Path $dest 'VERSION.txt')
        Write-Host "== $t 完成（$([math]::Round($sw.Elapsed.TotalMinutes, 1)) 分钟）=="
    }
    catch {
        $failed += $t
        Write-Warning "== $t 失败: $_ =="
    }
}

Write-Host "`n===== 汇总 ====="
foreach ($t in $tripletList) {
    $mark = if ($failed -contains $t) { '失败' } else { '成功' }
    Write-Host ("{0,-22} {1}" -f $t, $mark)
}
if ($failed.Count -gt 0) { exit 1 }
