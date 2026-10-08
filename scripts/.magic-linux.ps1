# X03：扫描 src/SshTool.App 下**全部** *.xaml 中的魔法数字（原先只扫 Views/Controls/Dialogs，
# 根目录的 MainPage.xaml 逃过了检查）。命中以下模式即报错并以非 0 退出（Themes/ 与 Views/Debug/ 豁免）：
#   十六进制颜色 #[0-9A-Fa-f]{6,8}、FontSize/Margin/Padding/Width/BorderThickness 的数字字面量
#   Padding/Margin/BorderThickness 绑到 Space*（x:Double）——UWP 不会把 Double 转成 Thickness，
#   运行时 XamlParseException（Token 画廊首例：TransientToast Padding=SpaceMd）
# 用法：pwsh scripts/check-magic-numbers.ps1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$patterns = @(
    '#[0-9A-Fa-f]{6,8}',
    'FontSize="\d',
    'Margin="\d',
    'Padding="\d',
    'Width="\d',
    'BorderThickness="\d',
    '(Padding|Margin|BorderThickness)="\{StaticResource Space(Xs|Sm|Md|Lg|Xl)\}'
)

$appDir = Join-Path $RepoRoot 'src/SshTool.App'
# Themes/ 是 Token 定义处，Views/Debug/ 是调试页，均豁免；obj/bin 是构建中间产物
$exempt = '/(Themes|obj|bin)/|/Views/Debug/'

$bad = @()
Get-ChildItem $appDir -Recurse -Filter *.xaml |
    Where-Object { $_.FullName -notmatch $exempt } |
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

if ($bad.Count -gt 0) {
    $bad | ForEach-Object { Write-Host $_ }
    Write-Host "`n共 $($bad.Count) 处魔法数字。颜色/字号/间距请改用 Themes/ 下的 Token（Themes/ 与 Views/Debug/ 豁免）。"
    exit 1
}
Write-Host "OK：src/SshTool.App 下（Themes/ 与 Views/Debug/ 除外）无魔法数字"
exit 0
