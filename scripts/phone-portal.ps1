# 通过 Windows Device Portal（USB 转发到本机 10080/10443）操作 Lumia：配对、列目录、拉文件。
# 目的：📱 验收产生的报告（LocalState\spike-reports\*.txt、logs\app.log）能直接取回 PC，
#       不用人对着手机屏幕抄数字。
#
#   pwsh scripts/phone-portal.ps1 -Pair 3XKF9Q       # 手机：设置→更新和安全→针对开发人员→设备发现→配对
#   pwsh scripts/phone-portal.ps1 -List              # 列 LocalState\spike-reports
#   pwsh scripts/phone-portal.ps1 -List -Path "\"    # 列 LocalState 根
#   pwsh scripts/phone-portal.ps1 -Pull              # 把 spike-reports 全部拉到 artifacts/phone-reports/
#   pwsh scripts/phone-portal.ps1 -Get app.log -Path "\logs"
#
# 会话 cookie 存在 .phone-portal-session.json（已 gitignore），配对一次后长期可用。
[CmdletBinding()]
param(
    [string]$Pair,
    [switch]$List,
    [switch]$Pull,
    [string]$Get,
    [string]$Path = '\spike-reports',
    [string]$BaseUrl = 'https://127.0.0.1:10443',
    [string]$PackageFamily = 'SshTool.LumiaSsh'
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$SessionFile = Join-Path $RepoRoot '.phone-portal-session.json'
$OutDir = Join-Path $RepoRoot 'artifacts\phone-reports'

function New-PortalSession {
    $s = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    if (Test-Path $SessionFile) {
        foreach ($c in (Get-Content $SessionFile -Raw | ConvertFrom-Json)) {
            $cookie = New-Object System.Net.Cookie($c.Name, $c.Value, $c.Path, $c.Domain)
            $s.Cookies.Add($cookie)
        }
    }
    return $s
}

function Save-PortalSession($session) {
    $uri = [Uri]$BaseUrl
    $cookies = $session.Cookies.GetCookies($uri) | ForEach-Object {
        [pscustomobject]@{ Name = $_.Name; Value = $_.Value; Path = $_.Path; Domain = $_.Domain }
    }
    $cookies | ConvertTo-Json -Depth 4 | Set-Content $SessionFile -Encoding UTF8
}

function Invoke-Portal([string]$relUrl, $session, [string]$outFile) {
    $url = "$BaseUrl$relUrl"
    $args = @{ Uri = $url; UseBasicParsing = $true; WebSession = $session; SkipCertificateCheck = $true; TimeoutSec = 30 }
    if ($outFile) { $args['OutFile'] = $outFile }
    return Invoke-WebRequest @args
}

# ---- 配对 ----
if ($Pair) {
    $session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
    $resp = Invoke-WebRequest -Uri "$BaseUrl/api/authorize/pair?pin=$Pair&persistent=1" `
        -Method Post -UseBasicParsing -WebSession $session -SkipCertificateCheck -TimeoutSec 30
    Write-Host "配对返回 HTTP $($resp.StatusCode)"
    Save-PortalSession $session
    Write-Host "会话已保存到 $SessionFile"
    exit 0
}

$session = New-PortalSession

# ---- 找到应用的 PackageFullName ----
$packages = (Invoke-Portal '/api/app/packagemanager/packages' $session).Content | ConvertFrom-Json
$pkg = $packages.InstalledPackages | Where-Object { $_.PackageFullName -like "$PackageFamily*" } | Select-Object -First 1
if (-not $pkg) {
    Write-Host "手机上没找到 $PackageFamily 的包。已安装的前 10 个："
    $packages.InstalledPackages | Select-Object -First 10 -ExpandProperty PackageFullName | ForEach-Object { "  $_" }
    exit 1
}
$pfn = $pkg.PackageFullName
Write-Host "应用包：$pfn"

$q = "knownfolderid=LocalAppData&packagefullname=$pfn&path=" + [Uri]::EscapeDataString($Path)

if ($List -or $Pull) {
    $files = (Invoke-Portal "/api/filesystem/apps/files?$q" $session).Content | ConvertFrom-Json
    if (-not $files.Items) { Write-Host "$Path 下没有文件"; exit 0 }
    $files.Items | ForEach-Object { "  {0,-44} {1,9} {2}" -f $_.Id, $_.SizeInBytes, $_.DateCreated }
    if ($Pull) {
        New-Item -ItemType Directory -Force $OutDir | Out-Null
        foreach ($f in $files.Items) {
            if ($f.Type -eq 16) { continue }   # 目录
            $target = Join-Path $OutDir $f.Id
            Invoke-Portal ("/api/filesystem/apps/file?$q&filename=" + [Uri]::EscapeDataString($f.Id)) $session $target | Out-Null
            Write-Host "  ↓ $target"
        }
    }
    exit 0
}

if ($Get) {
    New-Item -ItemType Directory -Force $OutDir | Out-Null
    $target = Join-Path $OutDir $Get
    Invoke-Portal ("/api/filesystem/apps/file?$q&filename=" + [Uri]::EscapeDataString($Get)) $session $target | Out-Null
    Write-Host "已下载：$target"
    exit 0
}

Write-Host "没指定动作。用 -Pair <PIN> / -List / -Pull / -Get <文件名>"
exit 0
