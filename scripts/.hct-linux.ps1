# Q04 / O12：扫描用户可见的硬编码文案。
#
# O12 之前这把尺子是坏的：它把「含中文的行」整行豁免（$chinesePattern 命中即
# continue），于是这个为「中文字面量必须资源化」而建的检查，恰好检不出中文字
# 面量；而且只扫 .xaml，不扫 .cs。verify ④b 一直是绿的，实际 XAML 里还有一百
# 多处中文。现在：
#   - 含中文的可见文案 = 命中（不再豁免）；
#   - .cs 里对用户可见属性赋中文字面量也算（日志字符串不算，日志不需要本地化）；
#   - 其余「疑似硬编码」的英文字面量沿用旧规则。
#
# C06 收尾（2026-09-27）新增 return 规则：O13 只扫「.属性 = "中文"」赋值形态，
# getter/辅助方法里 `return "中文"`（会话状态名、快捷键名、导出结果等 60 余处）
# 全部漏网。现补上：
#   - 行含 return 关键字且含中文字面量即命中（含拼接/三元；先剥掉行尾 // 注释，
#     避免 `return x; // 中文注释` 误报）；
#   - 行含 `Localized.` 或 `GetString(` 不算——两者都是「resw 为事实来源、中文
#     参数仅为资源缺失时的兜底」的既定写法（Localized.Get/Format、
#     AppServices.GetString、AccountSyncPage.GetString 等）；
#   - 已知限制：跨行 return（如 return string.Format(\n  "中文…")）的中文字面量
#     在续行上，本规则按行扫仍会漏；参数/字典形态（ConfirmDialog.ShowAsync("中文…")、
#     KeyBarKeyNames 字典等）同样不在本规则内——立为任务 O16。
#   - 仅入日志的 return（SettingsViewModel.Describe 的「无异常对象」、
#     NativeForwarder.FormatRouteDescription 的「转发」/「(远程)」——后者当前无
#     UI 消费者）按棘轮登记在基线里。
#
# O16a（2026-09-28）新增参数形态规则：代码里**任何**中文字面量（非注释行）
# 都算命中，除非该行属于日志调用（Logger.Log/LogFailure/AppLog/.Log(，
# 含多行调用的续行——用引号感知的括号深度跟踪判定）或含 Localized./GetString(
# （resw 兜底家族）。throw 的内部异常消息、Platform 桥的错误串等暂时进基线，
# 由 O16b/O16c 逐批清零或给出不算文案的理由。
#
# 区域豁免：注释行 `// gate:resw-fallback-start` … `// gate:resw-fallback-end`
# 之间跳过中文字面量检查。只用于「代码内的 resw 中文兜底表」这类例外
# （如 KeyBarLayoutEditorPage.FallbackOf、AddProbe 调用块的兜底实参），
# 豁免理由写在标记行旁。
#
# 棘轮（ratchet）：现存违例数量记在 $BaselinePath 里，按文件计数。
#   - 某文件违例数 > 基线 → 失败（新增了硬编码文案）；
#   - 某文件违例数 < 基线 → 提示「基线可收紧」，不失败；
#   - 基线里没有的文件出现违例 → 失败。
# O13 逐批清理时同步调小基线，清零后把基线文件清空即可。
# 用 -UpdateBaseline 把当前实际值写回基线（只在确认减少后使用）。
#
# 用法：pwsh scripts/check-hardcoded-text.ps1 [-UpdateBaseline]
[CmdletBinding()]
param([switch]$UpdateBaseline)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$BaselinePath = Join-Path $PSScriptRoot 'hardcoded-text-baseline.json'

$chinese = '[一-鿿]'

# XAML：用户可见的属性
$xamlAttrs = @(
    'Text', 'Content', 'Header', 'PlaceholderText', 'Description', 'Title',
    'PrimaryButtonText', 'SecondaryButtonText', 'CloseButtonText'
)
# 不可翻译的技术标识：产品名、格式示例。它们在中英界面下写法相同，
# 进资源表只会平添一份必须同步的重复。
$notTranslatable = @('#RRGGBB', 'tmux')

# 绑定/资源引用不算硬编码
$markup = '\{(Binding|x:Bind|StaticResource|ThemeResource|TemplateBinding)'

# C#：赋给用户可见属性的字面量。日志走 Logger.Log/AppLog.*，不在此列。
$csTargets = @(
    'Text', 'Content', 'Header', 'Title', 'Message', 'PlaceholderText',
    'PrimaryButtonText', 'SecondaryButtonText', 'CloseButtonText', 'Subtitle', 'Description'
)

$exemptPath = '/(obj|bin|AppPackages|Views/Debug)/'
$appDir = Join-Path $RepoRoot 'src/SshTool.App'

$hits = New-Object System.Collections.Generic.List[object]

function Add-Hit([string]$rel, [int]$lineNo, [string]$line, [string]$why) {
    $hits.Add([pscustomobject]@{
        File = $rel; Line = $lineNo; Why = $why; Text = $line.Trim()
    })
}

# 剥掉行尾 // 注释并统计引号外的圆括号增减：// 只有在引号外才是注释起点
# （"http://" 里的 // 在引号内）；括号深度供「日志调用续行」判定。
function Get-CodePart([string]$line, [ref]$parenDelta) {
    $inString = $false
    $delta = 0
    $cutIndex = -1
    for ($i = 0; $i -lt $line.Length; $i++) {
        $c = $line[$i]
        if ($inString) {
            if ($c -eq '"') { $inString = $false }
            continue
        }
        if ($c -eq '"') { $inString = $true; continue }
        if ($c -eq '(') { $delta++; continue }
        if ($c -eq ')') { $delta--; continue }
        if ($c -eq '/' -and ($i + 1) -lt $line.Length -and $line[$i + 1] -eq '/') { $cutIndex = $i; break }
    }
    $parenDelta.Value = $delta
    if ($cutIndex -ge 0) { return $line.Substring(0, $cutIndex) }
    return $line
}

# XAML 按**标签**扫，不按行：x:Uid 常写在标签首行、文案属性在后续行，
# 按行判断会把已本地化的元素整片误报（O13 批次 1 之后发现）。
$tagRe = [regex]'(?s)<[A-Za-z_][\w:.]*(?:\s+[\w:.]+\s*=\s*"[^"]*")*\s*/?>'

Get-ChildItem $appDir -Recurse -Include *.xaml, *.cs |
    Where-Object { $_.FullName -notmatch $exemptPath } |
    ForEach-Object {
        $file = $_
        $rel = $file.FullName.Substring($RepoRoot.Length + 1).Replace("/", "\")
        # 必须显式 UTF8：仓库源文件是无 BOM 的 UTF-8，PS5.1 的 Get-Content 默认按
        # ANSI(GBK) 解码会把多字节序列拼成伪 CJK 字（如 "—" 的 E2 80 94），
        # 造成中文误报与行号漂移（O16a 探针实测 TerminalWorkspace 行号差 29）。
        $text = Get-Content $file.FullName -Raw -Encoding UTF8
        if (-not $text) { return }

        if ($file.Extension -eq '.xaml') {
            foreach ($tag in $tagRe.Matches($text)) {
                $t = $tag.Value
                # 已有 x:Uid = 已本地化：resw 在运行时覆盖属性值，XAML 里的中文
                # 只是设计时兜底，不算硬编码。
                if ($t -match 'x:Uid\s*=') { continue }
                $lineNo = ($text.Substring(0, $tag.Index) -split "`n").Count
                foreach ($a in $xamlAttrs) {
                    foreach ($m in [regex]::Matches($t, ("(?<![\w:.]){0}=`"([^`"]*)`"" -f $a))) {
                        $value = $m.Groups[1].Value
                        if ($value -match $markup) { continue }
                        if ($notTranslatable -contains $value) { continue }
                        if ($value -match $chinese) {
                            Add-Hit $rel $lineNo ("{0}=`"{1}`"" -f $a, $value) "XAML $a 中文字面量"
                        }
                        # 非中文字面量只在「像一句文案」时才算：至少两个字符且含
                        # 字母。像 Text="/"、"…"、":" 这类符号是排版而非文案。
                        elseif ($value.Length -ge 2 -and $value -match '[A-Za-z]') {
                            Add-Hit $rel $lineNo ("{0}=`"{1}`"" -f $a, $value) "XAML $a 疑似硬编码"
                        }
                    }
                }
            }
        }
        else {
            $lineNo = 0
            $parenDepth = 0
            $logDepth = -1   # >=0 表示处于日志调用的括号内（起始行与续行都不算文案）
            $regionSkip = $false
            foreach ($line in ($text -split "`r?`n")) {
                $lineNo++
                if ($line -match '^\s*//') {
                    if ($line -match 'gate:resw-fallback-start') { $regionSkip = $true }
                    elseif ($line -match 'gate:resw-fallback-end') { $regionSkip = $false }
                    continue
                }
                $delta = 0
                $code = Get-CodePart $line ([ref]$delta)

                $hitThisLine = $false
                foreach ($t in $csTargets) {
                    if ($code -match ("\.{0}\s*=\s*`"([^`"]*)`"" -f $t)) {
                        if ($Matches[1] -match $chinese) {
                            Add-Hit $rel $lineNo $line "C# .$t 中文字面量"
                            $hitThisLine = $true
                        }
                    }
                }
                # return 形态（C06 收尾）：见文件头说明。
                if (-not $hitThisLine -and -not $regionSkip -and $code -match '\breturn\b' `
                    -and $code -notmatch '(Localized\.|GetString\()' -and $code -match $chinese) {
                    Add-Hit $rel $lineNo $line "C# return 中文文案"
                    $hitThisLine = $true
                }
                # 参数形态（O16a）：日志调用内或资源兜底参数不算，其余中文字面量都算。
                # Platform 桥自定义的 Log(op, what, watch) 助手无点前缀，用 \bLog\() 覆盖。
                $isLogLine = $code -match '(Logger\.|LogFailure|AppLog\.[A-Za-z]|\.Log\(|\bLog\()'
                if ($isLogLine -and $logDepth -lt 0) { $logDepth = $parenDepth }
                if (-not $hitThisLine -and -not $regionSkip -and $logDepth -lt 0 `
                    -and $code -notmatch '(Localized\.|GetString\()' -and $code -match $chinese) {
                    Add-Hit $rel $lineNo $line "C# 参数中文文案"
                }

                $parenDepth += $delta
                if ($logDepth -ge 0 -and $parenDepth -le $logDepth) { $logDepth = -1 }
            }
        }
    }

# ---- 按文件汇总 ----
$actual = @{}
foreach ($h in $hits) {
    if ($actual.ContainsKey($h.File)) { $actual[$h.File]++ } else { $actual[$h.File] = 1 }
}

if ($UpdateBaseline) {
    $ordered = [ordered]@{}
    foreach ($k in ($actual.Keys | Sort-Object)) { $ordered[$k] = $actual[$k] }
    ($ordered | ConvertTo-Json -Depth 3) | Set-Content -Path $BaselinePath -Encoding UTF8
    Write-Host "已写入基线：$($actual.Count) 个文件，共 $(($actual.Values | Measure-Object -Sum).Sum) 处"
    exit 0
}

$baseline = @{}
if (Test-Path $BaselinePath) {
    $json = Get-Content $BaselinePath -Raw | ConvertFrom-Json
    foreach ($p in $json.PSObject.Properties) { $baseline[$p.Name] = [int]$p.Value }
}

$regressions = New-Object System.Collections.Generic.List[string]
$improvements = New-Object System.Collections.Generic.List[string]

foreach ($f in ($actual.Keys | Sort-Object)) {
    $allowed = if ($baseline.ContainsKey($f)) { $baseline[$f] } else { 0 }
    if ($actual[$f] -gt $allowed) {
        $regressions.Add(("{0}：{1} 处 > 基线 {2}" -f $f, $actual[$f], $allowed))
        foreach ($h in ($hits | Where-Object { $_.File -eq $f })) {
            $regressions.Add(("    {0}:{1} [{2}] {3}" -f $h.File, $h.Line, $h.Why, $h.Text))
        }
    }
    elseif ($actual[$f] -lt $allowed) {
        $improvements.Add(("{0}：{1} 处 < 基线 {2}（可收紧基线）" -f $f, $actual[$f], $allowed))
    }
}
foreach ($f in ($baseline.Keys | Sort-Object)) {
    if (-not $actual.ContainsKey($f)) {
        $improvements.Add(("{0}：已清零（可从基线移除）" -f $f))
    }
}

$total = ($actual.Values | Measure-Object -Sum).Sum
if (-not $total) { $total = 0 }

if ($regressions.Count -gt 0) {
    $regressions | ForEach-Object { Write-Host $_ }
    Write-Host "`n新增硬编码文案。可见文案一律走 x:Uid + Strings\<lang>\Resources.resw（见 doc/06-OPT-AUDIT.md §3 P2-13）。"
    exit 1
}

if ($improvements.Count -gt 0) {
    $improvements | ForEach-Object { Write-Host $_ }
    Write-Host "（以上为好消息：跑 pwsh scripts/check-hardcoded-text.ps1 -UpdateBaseline 收紧基线）"
}
Write-Host "OK：无新增硬编码文案；待清理存量 $total 处（O13 逐批清零）"
exit 0
