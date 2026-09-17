# X06：对拍 C# SshErrorCode（src/SshTool.Core/Common/SshErrorCode.cs）与
# native/core/ssh/error_codes.h（01-DESIGN.md §6.3，数值必须完全一致）。
# 头文件尚未建立时（N01 之前）跳过并提示，返回 0。
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$csFile = Join-Path $RepoRoot 'src\SshTool.Core\Common\SshErrorCode.cs'
$hFile = Join-Path $RepoRoot 'native\core\ssh\error_codes.h'

if (-not (Test-Path $hFile)) {
    Write-Host "跳过：$hFile 不存在（N01 建立后本脚本开始对拍）"
    exit 0
}

# C#：枚举成员 Name = value
$csCodes = @{}
foreach ($line in Get-Content $csFile) {
    if ($line -match '^\s*(\w+)\s*=\s*(\d+)\s*,?\s*$') {
        $csCodes[$Matches[1]] = [int]$Matches[2]
    }
}
# C 头：#define SSH_ERR_<NAME> <value>
$hCodes = @{}
foreach ($line in Get-Content $hFile) {
    if ($line -match '^\s*#define\s+SSH_ERR_(\w+)\s+(\d+)') {
        $hCodes[$Matches[1]] = [int]$Matches[2]
    }
}

if ($csCodes.Count -eq 0) { Write-Host "失败：未能从 $csFile 解析出任何枚举值"; exit 1 }
if ($hCodes.Count -eq 0) { Write-Host "失败：未能从 $hFile 解析出任何 SSH_ERR_* 定义"; exit 1 }

$csSet = @($csCodes.Values | Sort-Object -Unique)
$hSet = @($hCodes.Values | Sort-Object -Unique)
$onlyCs = $csSet | Where-Object { $hSet -notcontains $_ }
$onlyH = $hSet | Where-Object { $csSet -notcontains $_ }

if ($onlyCs.Count -eq 0 -and $onlyH.Count -eq 0) {
    Write-Host ("OK：{0} 个错误码数值一致" -f $csSet.Count)
    exit 0
}
if ($onlyCs.Count -gt 0) { Write-Host ("仅 C# 有: " + ($onlyCs -join ', ')) }
if ($onlyH.Count -gt 0) { Write-Host ("仅 error_codes.h 有: " + ($onlyH -join ', ')) }
Write-Host "失败：两侧错误码不一致（01-DESIGN.md §6.3 要求完全一致）"
exit 1
