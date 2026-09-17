# SP04：取终端等宽字体（JetBrains Mono，SIL OFL 1.1）到 src/SshTool.App/Assets/Fonts/。
# 与 N01 的 fetch-third-party.ps1 同规矩：固定 URL + 固定 SHA256，校验不过就中止。
# 用法：pwsh scripts/fetch-fonts.ps1 [-Force]
[CmdletBinding()]
param([switch]$Force)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$OutDir = Join-Path $RepoRoot 'src\SshTool.App\Assets\Fonts'

$Url = 'https://github.com/JetBrains/JetBrainsMono/releases/download/v2.304/JetBrainsMono-2.304.zip'
$Sha256 = '6F6376C6ED2960EA8A963CD7387EC9D76E3F629125BC33D1FDCD7EB7012F7BBF'
# 压缩包内路径 → 落地文件名
$Wanted = @{
    'fonts/ttf/JetBrainsMono-Regular.ttf' = 'JetBrainsMono-Regular.ttf'
    'fonts/ttf/JetBrainsMono-Bold.ttf'    = 'JetBrainsMono-Bold.ttf'
    'OFL.txt'                             = 'OFL.txt'
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
$missing = $Wanted.Values | Where-Object { -not (Test-Path (Join-Path $OutDir $_)) }
if (-not $Force -and -not $missing) {
    Write-Host "字体已就位（$OutDir），跳过下载。用 -Force 强制重取。"
    exit 0
}

$zip = Join-Path ([IO.Path]::GetTempPath()) 'JetBrainsMono-2.304.zip'
if (-not (Test-Path $zip)) {
    Write-Host "下载 $Url"
    Invoke-WebRequest $Url -OutFile $zip -UseBasicParsing
}

$actual = (Get-FileHash $zip -Algorithm SHA256).Hash
if ($actual -ne $Sha256) {
    Remove-Item $zip -Force
    throw "SHA256 不符：期望 $Sha256，实际 $actual（已删除下载文件）"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    foreach ($entry in $Wanted.GetEnumerator()) {
        $item = $archive.Entries | Where-Object { $_.FullName -eq $entry.Key }
        if (-not $item) { throw "压缩包内找不到 $($entry.Key)" }
        $target = Join-Path $OutDir $entry.Value
        [IO.Compression.ZipFileExtensions]::ExtractToFile($item, $target, $true)
        Write-Host ("  {0,-32} {1,8:N0} 字节" -f $entry.Value, (Get-Item $target).Length)
    }
}
finally {
    $archive.Dispose()
}
Write-Host "OK：字体已落地到 $OutDir"
exit 0
