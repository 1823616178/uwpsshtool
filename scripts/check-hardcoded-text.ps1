# Q04：扫描 src/SshTool/App/Views/**/*.xaml 中的硬编码文案（Text=" / Content=" / Header=" 字面量）。
# 豁免：x:Uid、{Binding、{x:Bind、{StaticResource、{ThemeResource、Views/Debug/ 路径、含中文的行。
# 用法：pwsh scripts/check-hardcoded-text.ps1
# 兼容 PS 5.1 与 pwsh 7（未使用 pwsh 7 独有语法）。
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot

# 匹配 Text="..." / Content="..." / Header="..." 字面量（空值也匹配，空值无意义但不误报）
$patterns = @(
    'Text="[^"]*"',
    'Content="[^"]*"',
    'Header="[^"]*"'
)

# 豁免模式：含以下任一子串即跳过
$exemptPatterns = @(
    'x:Uid',
    '\{Binding',
    '\{x:Bind',
    '\{StaticResource',
    '\{ThemeResource',
    '\{TemplateBinding'
)

# Views/Debug/ 整文件夹豁免；obj/bin 是构建中间产物；含中文的行豁免（C-06 剩余，由本脚本之外的任务清理）
$exemptPath = '\\(Views\\Debug|obj|bin)\\'
$chinesePattern = '[\u4e00-\u9fff]'

$appDir = Join-Path $RepoRoot 'src\SshTool.App'
$bad = @()
Get-ChildItem $appDir -Recurse -Filter *.xaml |
    Where-Object { $_.FullName -notmatch $exemptPath } |
    ForEach-Object {
        $file = $_
        $rel = $file.FullName.Substring($RepoRoot.Length + 1)
        $lineNo = 0
        foreach ($line in Get-Content $file.FullName) {
            $lineNo++
            # 含中文的行豁免
            if ($line -match $chinesePattern) { continue }
            # 含豁免模式跳过
            $exempt = $false
            foreach ($ep in $exemptPatterns) {
                if ($line -match $ep) { $exempt = $true; break }
            }
            if ($exempt) { continue }
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
    Write-Host "`n共 $($bad.Count) 处潜在硬编码文案。Text/Content/Header 字面量请改用 x:Uid + resw 双语。"
    Write-Host "豁免：x:Uid、{{Binding、{{x:Bind、{{StaticResource、{{ThemeResource、Views/Debug/、含中文行。"
    exit 1
}
Write-Host "OK：src/SshTool/App/Views/（Views/Debug/ 除外）无潜在硬编码文案"
exit 0
