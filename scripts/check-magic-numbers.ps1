# X03：扫描 src/SshTool.App/{Views,Controls,Dialogs}/**/*.xaml 中的魔法数字。
# 命中以下模式即报错并以非 0 退出（Themes/ 与 Views/Debug/ 豁免）：
#   十六进制颜色 #[0-9A-Fa-f]{6,8}、FontSize/Margin/Padding/Width 的数字字面量
# 用法：pwsh scripts/check-magic-numbers.ps1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$dirs = @('Views', 'Controls', 'Dialogs')
$patterns = @(
    '#[0-9A-Fa-f]{6,8}',
    'FontSize="\d',
    'Margin="\d',
    'Padding="\d',
    'Width="\d'
)

$bad = @()
foreach ($d in $dirs) {
    $dir = Join-Path $RepoRoot "src\SshTool.App\$d"
    if (-not (Test-Path $dir)) { continue }
    Get-ChildItem $dir -Recurse -Filter *.xaml |
        Where-Object { $_.FullName -notmatch '\\Debug(\\|$)' } |
        ForEach-Object {
            $file = $_
            $rel = $file.FullName.Substring($RepoRoot.Length + 1)
            $lineNo = 0
            foreach ($line in Get-Content $file.FullName) {
                $lineNo++
                foreach ($p in $patterns) {
                    if ($line -match $p) {
                        $bad += ("{0}:{1}: 命中 /{2}/ → {3}" -f $rel, $lineNo, $p, $line.Trim())
                        break
                    }
                }
            }
        }
}

if ($bad.Count -gt 0) {
    $bad | ForEach-Object { Write-Host $_ }
    Write-Host "`n共 $($bad.Count) 处魔法数字。颜色/字号/间距请改用 Themes/ 下的 Token（Themes/ 与 Views/Debug/ 豁免）。"
    exit 1
}
Write-Host "OK：Views/Controls/Dialogs 下无魔法数字"
exit 0
