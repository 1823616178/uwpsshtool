# N01/SP03：抓取第三方 C 源码到 native/third_party/。固定 URL + 固定 SHA256，校验不过即中止。
# 抓回来的源码**不入库**（.gitignore）；我们自己写的 UWP 配置头与补丁说明才入库。
#
#   pwsh scripts/fetch-third-party.ps1            # 缺什么抓什么
#   pwsh scripts/fetch-third-party.ps1 -Force     # 全部重抓
#   pwsh scripts/fetch-third-party.ps1 -List      # 只列清单与状态
[CmdletBinding()]
param([switch]$Force, [switch]$List)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Root = Join-Path $RepoRoot 'native\third_party'

# SP03 先只要 libssh2；libvterm 0.3.3 与 argon2 20190702 在 N01 补进来
$Packages = @(
    [pscustomobject]@{
        Name    = 'libssh2'
        Version = '1.11.1'
        Url     = 'https://github.com/libssh2/libssh2/releases/download/libssh2-1.11.1/libssh2-1.11.1.tar.gz'
        Sha256  = 'D9EC76CBE34DB98EEC3539FE2C899D26B0C837CB3EB466A56B0F109CABF658F7'
        # 压缩包内顶层目录，解包后重命名为 Name
        TopDir  = 'libssh2-1.11.1'
        Probe   = 'include\libssh2.h'
    }
)

New-Item -ItemType Directory -Force $Root | Out-Null

foreach ($p in $Packages) {
    $dest = Join-Path $Root $p.Name
    $have = Test-Path (Join-Path $dest $p.Probe)

    if ($List) {
        "{0,-10} {1,-10} {2}" -f $p.Name, $p.Version, $(if ($have) { "已就位 $dest" } else { '缺失' })
        continue
    }
    if ($have -and -not $Force) {
        Write-Host "$($p.Name) $($p.Version) 已就位，跳过（-Force 可重抓）"
        continue
    }

    $archive = Join-Path ([IO.Path]::GetTempPath()) (Split-Path $p.Url -Leaf)
    if (-not (Test-Path $archive)) {
        Write-Host "下载 $($p.Url)"
        Invoke-WebRequest $p.Url -OutFile $archive -UseBasicParsing
    }
    $actual = (Get-FileHash $archive -Algorithm SHA256).Hash
    if ($actual -ne $p.Sha256) {
        Remove-Item $archive -Force
        throw "$($p.Name) SHA256 不符：期望 $($p.Sha256)，实际 $actual（已删除下载文件）"
    }

    if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
    $staging = Join-Path ([IO.Path]::GetTempPath()) ("tp-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force $staging | Out-Null
    try {
        # Windows 10 自带 bsdtar，能直接解 .tar.gz
        & tar.exe -xzf $archive -C $staging
        if ($LASTEXITCODE -ne 0) { throw "解包失败（tar 退出码 $LASTEXITCODE）" }
        Move-Item (Join-Path $staging $p.TopDir) $dest
    }
    finally {
        Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
    }

    $files = (Get-ChildItem $dest -Recurse -File).Count
    Write-Host ("  {0} {1} → {2}（{3} 个文件）" -f $p.Name, $p.Version, $dest, $files)
}

if (-not $List) { Write-Host "OK：第三方源码就位于 $Root（不入库，见 .gitignore）" }
exit 0
