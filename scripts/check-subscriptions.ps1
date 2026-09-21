# O03：事件订阅对称性检查（05-CODE-AUDIT §C-01 的可执行版本）。
#
# 背景：页面与控件随导航反复重建，而仓库 / SyncCoordinator / SessionManager /
# ServiceRegistry 单例 VM 活得和进程一样久。页面级对象订阅了这些长寿对象却不解除，
# 就会按「访问次数」累积死页面与死 VM——这正是 O03 修掉的两条泄漏路径。
#
# 规则（只管长寿事件，故意不覆盖控件自己的子元素事件，避免误报）：
#   A. `xxx.<Event> += 具名处理器;` 必须在同一文件里有 `-= 具名处理器;`；
#   B. `xxx.<Event> += (s, e) => ...` / `+= delegate` 一律报错：匿名委托解不掉。
# 受管事件名见 $WatchedEvents。
#
# 豁免：Views/Debug/（调试页不进正式包）、obj/bin，以及 $Exempt 里逐条注明理由的行。
# 用法：pwsh scripts/check-subscriptions.ps1
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot

# 只盯应用级长寿对象会抛的事件；PropertyChanged 不在内（页面订阅自己的 VM 占绝大多数，
# 两者同生同死，纳入只会制造噪声）——但订阅**单例 VM** 的 PropertyChanged 属于真漏，
# 由 $SingletonViewModels 单独兜。
$WatchedEvents = @(
    'Changed',
    'StateChanged',
    'SessionsChanged',
    'StatusesChanged'
)

# 经 ServiceRegistry 共享的单例 VM：订阅它们的任何事件都必须解除。
$SingletonViewModels = @(
    'SessionsPaneViewModel',
    'WorkspaceViewModel'
)

# 逐条豁免：'文件名:事件:处理器（匿名为 lambda）' => 理由。
# 只放「订阅方与被订阅方同寿命」的情形——那不是泄漏，解不解都一样。
$Exempt = @{
    'SessionsPaneViewModel.cs:SessionsChanged:lambda' =
        '本 VM 自己就是 ServiceRegistry 单例（MainViewModel 注册），与 SessionManager 同寿命'
    'WorkspaceViewModel.cs:SessionsChanged:OnSessionsChanged' =
        '本 VM 是 ServiceRegistry 单例（MainPage.EnsureWorkspace），与 SessionManager 同寿命'
    'AppServices.cs:Changed:OnAgentTimeoutChanged' =
        'AppServices 是应用级服务容器本身，与 SettingsRepository 同寿命'
    'AppServices.cs:Changed:lambda' =
        '同上：应用级容器订阅应用级仓库'
    'NativeForwarder.cs:StateChanged:lambda' =
        '订阅的是本转发器自建自管的专用会话，ActiveTunnel.Dispose 一并释放（见该文件 :34-46）'
}

$scanDirs = @('ViewModels', 'Views', 'Controls', 'Terminal', 'Infrastructure', 'Platform')
$appDir = Join-Path $RepoRoot 'src\SshTool.App'
$bad = @()

function Test-Exempt([string]$path, [string]$ev, [string]$handler) {
    return $Exempt.ContainsKey(("{0}:{1}:{2}" -f (Split-Path -Leaf $path), $ev, $handler))
}

foreach ($dir in $scanDirs) {
    $full = Join-Path $appDir $dir
    if (-not (Test-Path $full)) { continue }
    Get-ChildItem $full -Recurse -Filter *.cs |
        Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' -and $_.FullName -notmatch '\\Views\\Debug\\' } |
        ForEach-Object {
            $file = $_
            $rel = $file.FullName.Substring($RepoRoot.Length + 1)
            $text = Get-Content $file.FullName -Raw
            $lines = Get-Content $file.FullName
            $lineNo = 0
            foreach ($line in $lines) {
                $lineNo++
                # 匿名委托订阅：能匹配到受管事件就报错
                foreach ($ev in $WatchedEvents) {
                    if ($line -match "\.$ev\s*\+=\s*(\(|delegate|async)") {
                        if (Test-Exempt $rel $ev 'lambda') { continue }
                        $bad += "{0}:{1}: {2} 用匿名委托订阅，永远解不掉 → 改具名处理器并在 Detach/OnNavigatedFrom 解除`n    {3}" -f $rel, $lineNo, $ev, $line.Trim()
                    }
                }
                # 单例 VM 的 PropertyChanged/CollectionChanged：匿名订阅即泄漏
                # （O03 的 SessionsPivot 就是这么漏的）。
                foreach ($vm in $SingletonViewModels) {
                    if ($text -match [regex]::Escape($vm) `
                        -and $line -match '\.(PropertyChanged|CollectionChanged)\s*\+=\s*(\(|delegate|async)') {
                        if (Test-Exempt $rel 'PropertyChanged' 'lambda') { continue }
                        $bad += "{0}:{1}: 对单例 VM（{2}）用匿名委托订阅，永远解不掉`n    {3}" -f $rel, $lineNo, $vm, $line.Trim()
                    }
                }
                # 具名处理器订阅：同文件内必须有配对的 -=
                foreach ($ev in $WatchedEvents) {
                    if ($line -match "\.$ev\s*\+=\s*([A-Za-z_][A-Za-z0-9_]*)\s*;") {
                        $handler = $Matches[1]
                        if (Test-Exempt $rel $ev $handler) { continue }
                        if ($text -notmatch "-=\s*$([regex]::Escape($handler))\s*;") {
                            $bad += "{0}:{1}: 订阅 {2} 的 {3} 在本文件内没有配对的 -= → 补 Detach()/OnNavigatedFrom 解除`n    {4}" -f $rel, $lineNo, $ev, $handler, $line.Trim()
                        }
                    }
                }
            }
        }
}

$bad = $bad | Select-Object -Unique
if ($bad.Count -gt 0) {
    $bad | ForEach-Object { Write-Host $_ }
    Write-Host "`n共 $($bad.Count) 处订阅未解除。页面/控件/VM 的生命周期短于仓库与协调器，必须成对解除（见 doc/06-OPT-AUDIT.md §3 P0-2）。"
    exit 1
}
Write-Host "OK：src/SshTool.App 下（Views/Debug/ 除外）受管事件订阅均有配对解除"
exit 0
