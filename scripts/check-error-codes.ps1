# N08：对拍 C# SshErrorCode（src/SshTool.Core/Common/SshErrorCode.cs）与
# native error_codes.h 的 kSshErrorCode* 常量（01-DESIGN.md §6.3）。
#
# 两侧逐项比对「成员名 = 数值」：C# 成员名 X 对应 native 常量
# kSshErrorCodeX。任何一侧多出/缺失成员或同名不同值都列出差异并返回 1。
# verify.ps1 的门禁步骤调用本脚本。
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$csFile = Join-Path $RepoRoot 'src\SshTool.Core\Common\SshErrorCode.cs'
$hFile = Join-Path $RepoRoot 'native\core\ssh\error_codes.h'

if (-not (Test-Path $csFile)) { Write-Host "失败：$csFile 不存在"; exit 1 }
if (-not (Test-Path $hFile)) { Write-Host "失败：$hFile 不存在（N08 建立后必须存在）"; exit 1 }

# C#：枚举成员行「    Name = 123,」（纯大括弧外，跳过注释/空行）
$csCodes = [ordered]@{}
foreach ($line in Get-Content $csFile) {
    if ($line -match '^\s*([A-Za-z]\w*)\s*=\s*(\d+)\s*,?\s*(//.*)?$') {
        $csCodes[$Matches[1]] = [int]$Matches[2]
    }
}
# native：「inline constexpr int kSshErrorCode<Name> = 123;」
$hCodes = [ordered]@{}
foreach ($line in Get-Content $hFile) {
    if ($line -match '^\s*inline\s+constexpr\s+int\s+kSshErrorCode(\w+)\s*=\s*(\d+)\s*;') {
        $hCodes[$Matches[1]] = [int]$Matches[2]
    }
}

if ($csCodes.Count -eq 0) { Write-Host "失败：未能从 $csFile 解析出任何枚举值（格式变了？请同步本脚本）"; exit 1 }
if ($hCodes.Count -eq 0) { Write-Host "失败：未能从 $hFile 解析出任何 kSshErrorCode* 常量（格式变了？请同步本脚本）"; exit 1 }

$failed = $false
# 逐项比对：C# 侧每个成员都要在 native 侧找到同名同值常量
foreach ($name in $csCodes.Keys) {
    if (-not $hCodes.Contains($name)) {
        Write-Host ("仅 C# 有: {0} = {1}" -f $name, $csCodes[$name])
        $failed = $true
    } elseif ($hCodes[$name] -ne $csCodes[$name]) {
        Write-Host ("数值不一致: {0}  C# = {1}  native = {2}" -f $name, $csCodes[$name], $hCodes[$name])
        $failed = $true
    }
}
foreach ($name in $hCodes.Keys) {
    if (-not $csCodes.Contains($name)) {
        Write-Host ("仅 native 有: {0} = {1}" -f $name, $hCodes[$name])
        $failed = $true
    }
}

if ($failed) {
    Write-Host "失败：两侧错误码不一致（01-DESIGN.md §6.3 要求完全一致；native 常量命名 kSshErrorCode<C#成员名>）"
    exit 1
}
Write-Host ("OK：{0} 个错误码逐项一致" -f $csCodes.Count)
exit 0
