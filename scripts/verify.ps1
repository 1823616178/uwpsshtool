# X06：质量门禁（01-DESIGN.md §13）。
#   pwsh scripts/verify.ps1           全量：① dotnet test ② native ctest ③ 错误码对拍 ④ 魔法数字 ⑤ App x64 Debug（含 WMC0151 契约检查）
#   pwsh scripts/verify.ps1 -Quick    只跑 ①②
#   pwsh scripts/verify.ps1 -Arm      追加 ARM Release（.NET Native）构建
# 每步计时；某步失败即打印汇总并指出该步骤，以非 0 退出。
[CmdletBinding()]
param(
    [switch]$Quick,
    [switch]$Arm
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$results = New-Object System.Collections.Generic.List[object]

# 某些受控终端只继承用户级环境变量，进程里会缺 PROCESSOR_ARCHITECTURE。
# VS 的 AppxPackage 任务未做 null 守卫，会因此在 MrmSupportLibrary.GetLocation 空引用。
if ([string]::IsNullOrWhiteSpace($env:PROCESSOR_ARCHITECTURE)) {
    $machineArchitecture = [Environment]::GetEnvironmentVariable(
        'PROCESSOR_ARCHITECTURE', [EnvironmentVariableTarget]::Machine)
    if ([string]::IsNullOrWhiteSpace($machineArchitecture)) {
        $machineArchitecture = if ([Environment]::Is64BitOperatingSystem) { 'AMD64' } else { 'x86' }
    }
    $env:PROCESSOR_ARCHITECTURE = $machineArchitecture
}

function Find-MsBuild([string]$range) {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere)) { throw "找不到 vswhere：$vswhere" }
    $found = & $vswhere -version $range -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' |
        Select-Object -First 1
    if (-not $found) { throw "vswhere 未找到版本范围 $range 的 MSBuild" }
    return $found
}

function Find-CMake {
    $cmd = Get-Command cmake -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    # 回退：vcpkg 下载的 cmake（SP02）
    $fallback = Get-ChildItem (Join-Path $RepoRoot 'tools\vcpkg\downloads\tools\cmake-*\cmake-*\bin\cmake.exe') -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($fallback) { return $fallback.FullName }
    throw "找不到 cmake（PATH 与 tools/vcpkg 缓存均无）"
}

function Write-Summary {
    Write-Host "`n===== 汇总 ====="
    $results | Format-Table -AutoSize | Out-Host
}

# 步骤体内的原生命令之后用 Assert-ExitOk 及时中止该步骤
function Assert-ExitOk([string]$what) {
    if ($LASTEXITCODE -ne 0) { throw "$what 退出码 $LASTEXITCODE" }
}

function Invoke-Step([string]$name, [scriptblock]$body) {
    Write-Host "`n===== $name ====="
    $global:LASTEXITCODE = 0
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        & $body
        Assert-ExitOk $name
        $sw.Stop()
        $results.Add([pscustomobject]@{ Step = $name; Result = '通过'; Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1) })
    }
    catch {
        $sw.Stop()
        $results.Add([pscustomobject]@{ Step = $name; Result = "失败：$_"; Seconds = [math]::Round($sw.Elapsed.TotalSeconds, 1) })
        Write-Summary
        Write-Host "步骤失败：$name"
        exit 1
    }
}

Push-Location $RepoRoot
try {
    Invoke-Step '① dotnet test（Core）' {
        dotnet test tests/SshTool.Core.Tests -v:q -nologo
    }

    $cmake = Find-CMake
    $ctest = Join-Path (Split-Path -Parent $cmake) 'ctest.exe'
    Invoke-Step '② native 宿主机测试（cmake + ctest）' {
        & $cmake -S native/tests -B native/tests/build -A x64
        Assert-ExitOk 'cmake configure'
        & $cmake --build native/tests/build --config Release
        Assert-ExitOk 'cmake build'
        & $ctest --test-dir native/tests/build -C Release --output-on-failure
        Assert-ExitOk 'ctest'
    }

    if (-not $Quick) {
        Invoke-Step '③ 错误码对拍' {
            & $PSHOME\pwsh.exe -NoProfile -File scripts/check-error-codes.ps1
        }
        # 2026-09-18 事故：一句 open(p,'w').write(open(p).read()) 把 04-TASKS.md 写成 0 字节并提交了两次。
        # 文档是本项目的事实来源，空文件/结构被毁必须当场暴露。
        Invoke-Step '④a 文档完整性' {
            $docs = Get-ChildItem (Join-Path $RepoRoot 'doc') -Filter *.md
            foreach ($d in $docs) {
                $lines = (Get-Content $d.FullName | Measure-Object -Line).Lines
                if ($lines -lt 20) { throw "$($d.Name) 只有 $lines 行，疑似被写坏" }
            }
            $tasks = Join-Path $RepoRoot 'doc\04-TASKS.md'
            $all = (Select-String -Path $tasks -Pattern '^- \[( |x)\] \*\*[A-Z]+\d+' -AllMatches).Count
            if ($all -ne 111) { throw "04-TASKS.md 的任务条目数为 $all，应为 111（文件可能被截断或误改）" }
            Write-Host "OK：doc/*.md 共 $($docs.Count) 份，04-TASKS 任务条目 $all"
        }

        Invoke-Step '④ XAML 魔法数字' {
            & $PSHOME\pwsh.exe -NoProfile -File scripts/check-magic-numbers.ps1
        }

        # 两段式构建：VS2017(v141) 构建 Native，VS2026 构建 sln（见 doc/ENV.md §5）
        # -nr:false：不留 MSBuild 常驻节点。节点会攥住 obj\ARM\Release\ilc\ilclog.csv 等文件，
        # 之后在 VS 里构建同一配置会报「ilc.exe 未能运行……正由另一进程使用」。
        $msbuildNative = Find-MsBuild '[15.0,16.0)'
        $msbuildSln = Find-MsBuild '[17.0,)'
        Invoke-Step '⑤ App x64 Debug（两段式）' {
            & $msbuildNative src/SshTool.Native/SshTool.Native.vcxproj -p:Configuration=Debug -p:Platform=x64 -v:m -nologo -nr:false
            Assert-ExitOk 'Native x64 Debug'
            & $msbuildSln SshTool.sln -p:Configuration=Debug -p:Platform=x64 -v:m -nologo -nr:false |
                Tee-Object -Variable slnLog
            Assert-ExitOk 'sln x64 Debug'
            # WMC0151：XAML 用了高于 TargetPlatformMinVersion(15063) 的 API。编译器只警告，
            # 桌面 x64 不崩，但 W10M 真机加载该 XAML 时抛 XamlParseException 0x802B000A。
            $overContract = $slnLog | Select-String 'WMC0151'
            if ($overContract) {
                throw ("XAML 越过 15063 契约（WMC0151），W10M 真机会抛 XamlParseException：`n" +
                       (($overContract | ForEach-Object { $_.Line.Trim() }) -join "`n"))
            }
        }

        if ($Arm) {
            Invoke-Step '⑥ ARM Release（.NET Native，两段式）' {
                & $msbuildNative src/SshTool.Native/SshTool.Native.vcxproj -p:Configuration=Release -p:Platform=ARM -v:m -nologo -nr:false
                Assert-ExitOk 'Native ARM Release'
                & $msbuildSln SshTool.sln -p:Configuration=Release -p:Platform=ARM -v:m -nologo -nr:false
                Assert-ExitOk 'sln ARM Release'
            }
        }
    }

    Write-Summary
    Write-Host '全部通过'
    exit 0
}
finally {
    Pop-Location
}
