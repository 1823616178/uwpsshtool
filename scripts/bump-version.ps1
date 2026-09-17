# 递增（或指定）Package.appxmanifest 里的包版本。
# 为什么需要：所有构建都叫 0.1.0.0 时，旁加载同版本号可能不替换旧包，界面上也分不出
# 手机上跑的是哪一次构建（SP04 就为此返工过一轮）。每出一次真机包就 bump 一下。
#
#   pwsh scripts/bump-version.ps1              # 修订号 +1，如 0.1.0.3 → 0.1.0.4
#   pwsh scripts/bump-version.ps1 -Set 0.2.0.0 # 指定版本
#   pwsh scripts/bump-version.ps1 -Show        # 只看当前版本
[CmdletBinding()]
param(
    [string]$Set,
    [switch]$Show
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Manifest = Join-Path $RepoRoot 'src\SshTool.App\Package.appxmanifest'

[xml]$doc = Get-Content $Manifest -Raw
$identity = $doc.Package.Identity
$current = $identity.Version

if ($Show) { Write-Host $current; exit 0 }

if ($Set) {
    $parsed = [Version]$Set
    $next = $Set
}
else {
    $v = [Version]$current
    $next = "{0}.{1}.{2}.{3}" -f $v.Major, $v.Minor, $v.Build, ($v.Revision + 1)
}

$identity.Version = $next
# 保持原有格式（UTF-8 无 BOM、缩进 2）
$settings = New-Object System.Xml.XmlWriterSettings
$settings.Indent = $true
$settings.IndentChars = '  '
$settings.Encoding = New-Object System.Text.UTF8Encoding($false)
$writer = [System.Xml.XmlWriter]::Create($Manifest, $settings)
try { $doc.Save($writer) } finally { $writer.Dispose() }

Write-Host "包版本 $current → $next"
Write-Host "记得重新构建并安装：Release/ARM 包在 src\SshTool.App\AppPackages\"
exit 0
