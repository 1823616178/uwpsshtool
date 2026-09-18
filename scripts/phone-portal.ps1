# 通过 Windows Device Portal（USB 转发到本机 10080/10443）操作 Lumia：配对、列目录、拉文件。
# 目的：📱 验收产生的报告（LocalState\spike-reports\*.txt、logs\app.log）能直接取回 PC，
#       不用人对着手机屏幕抄数字。
#
#   pwsh scripts/phone-portal.ps1 -Pair 3XKF9Q       # 手机：设置→更新和安全→针对开发人员→设备发现→配对
#   pwsh scripts/phone-portal.ps1 -List              # 列 LocalState\spike-reports
#   pwsh scripts/phone-portal.ps1 -List -Path "\LocalState" # 列 LocalState 根
#   pwsh scripts/phone-portal.ps1 -Pull              # 把 spike-reports 全部拉到 artifacts/phone-reports/
#   pwsh scripts/phone-portal.ps1 -Get app.log -Path "\LocalState\logs"
#   pwsh scripts/phone-portal.ps1 -Install src\SshTool.App\AppPackages\...\SshTool.App_0.1.0.1_ARM.appx
#   pwsh scripts/phone-portal.ps1 -Push .\ssh-autotest.json # 上传到应用自建报告目录（继承可读 ACL）
#
# 会话 cookie 存在 .phone-portal-session.json（已 gitignore），配对一次后长期可用。
[CmdletBinding()]
param(
    [string]$Pair,
    [switch]$List,
    [switch]$Pull,
    [string]$Get,
    [string]$Install,
    [string]$Push,
    # knownfolderid=LocalAppData 的根是包目录，不是 LocalState；必须显式带 LocalState。
    [string]$Path = '\LocalState\spike-reports',
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

# 门户的 POST 有 CSRF 校验：GET 任意接口时它下发 Set-Cookie: CSRF-Token，
# 但 PS7 的 WebSession 不收这个 cookie——手工抠出来，POST 时同时放请求头和 Cookie。
function Get-CsrfHeaders($session) {
    $probe = Invoke-Portal '/api/app/packagemanager/packages' $session
    $setCookie = $probe.Headers['Set-Cookie'] -join ';'
    if ($setCookie -match 'CSRF-Token=([^;,\s]+)') {
        $token = $Matches[1]
        $session.Cookies.Add((New-Object System.Net.Cookie('CSRF-Token', $token, '/', ([Uri]$BaseUrl).Host)))
        return @{ 'X-CSRF-Token' = $token }
    }
    Write-Host '警告：没拿到 CSRF-Token，POST 可能 403'
    return @{}
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

# ---- 安装应用包（含同目录 Dependencies\arm\*.appx）----
if ($Install) {
    $appx = Get-Item $Install
    $form = @{ ($appx.Name) = $appx }
    $depDir = Join-Path $appx.Directory.FullName 'Dependencies\arm'
    if (Test-Path $depDir) {
        foreach ($d in Get-ChildItem $depDir -Filter *.appx) { $form[$d.Name] = $d }
    }
    Write-Host ("上传 {0} 与 {1} 个依赖包…" -f $appx.Name, ($form.Count - 1))
    $installHeaders = Get-CsrfHeaders $session
    $r = Invoke-WebRequest -Uri "$BaseUrl/api/app/packagemanager/package?package=$([Uri]::EscapeDataString($appx.Name))" `
        -Method Post -Form $form -WebSession $session -SkipCertificateCheck -TimeoutSec 600 -UseBasicParsing -Headers $installHeaders
    Write-Host "上传返回 HTTP $($r.StatusCode)，等待安装完成…"
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Seconds 3
        try {
            $st = Invoke-WebRequest "$BaseUrl/api/app/packagemanager/state" -WebSession $session -SkipCertificateCheck -UseBasicParsing -TimeoutSec 30
            if ($st.StatusCode -eq 200) { Write-Host "安装完成：$($st.Content)"; exit 0 }
        }
        catch {
            $resp = $_.Exception.Response
            if ($resp -and [int]$resp.StatusCode -eq 204) { Write-Host '安装中…' }
            else { Write-Host "状态：$($_.Exception.Message.Split([char]10)[0])" }
        }
    }
    Write-Host '等待超时，请在手机上确认'
    exit 1
}

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

# ---- 上传文件到包数据目录 <Path>（SP03：把 ssh-autotest.json 推进无人值守测试）----
if ($Push) {
    $item = Get-Item $Push
    # 字段名必须是字面量 "file"（用文件名当字段名会被门户判成 "No files posted"，2026-09-18 实测）
    $form = @{ 'file' = $item }
    $uq = "$q&filename=" + [Uri]::EscapeDataString($item.Name)
    $headers = Get-CsrfHeaders $session
    $r = Invoke-WebRequest -Uri "$BaseUrl/api/filesystem/apps/file?$uq" `
        -Method Post -Form $form -WebSession $session -SkipCertificateCheck -TimeoutSec 120 -UseBasicParsing -Headers $headers
    Write-Host "上传 $($item.Name) → $Path，HTTP $($r.StatusCode)"
    exit 0
}

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
