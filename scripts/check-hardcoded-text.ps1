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

$exemptPath = '\\(obj|bin|AppPackages|Views\\Debug)\\'
$appDir = Join-Path $RepoRoot 'src\SshTool.App'

$hits = New-Object System.Collections.Generic.List[object]

function Add-Hit([string]$rel, [int]$lineNo, [string]$line, [string]$why) {
    $hits.Add([pscustomobject]@{
        File = $rel; Line = $lineNo; Why = $why; Text = $line.Trim()
    })
}

# XAML 按**标签**扫，不按行：x:Uid 常写在标签首行、文案属性在后续行，
# 按行判断会把已本地化的元素整片误报（O13 批次 1 之后发现）。
$tagRe = [regex]'(?s)<[A-Za-z_][\w:.]*(?:\s+[\w:.]+\s*=\s*"[^"]*")*\s*/?>'

Get-ChildItem $appDir -Recurse -Include *.xaml, *.cs |
    Where-Object { $_.FullName -notmatch $exemptPath } |
    ForEach-Object {
        $file = $_
        $rel = $file.FullName.Substring($RepoRoot.Length + 1)
        $text = Get-Content $file.FullName -Raw
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
            foreach ($line in ($text -split "`r?`n")) {
                $lineNo++
                if ($line -match '^\s*//') { continue }
                foreach ($t in $csTargets) {
                    if ($line -match ("\.{0}\s*=\s*`"([^`"]*)`"" -f $t)) {
                        if ($Matches[1] -match $chinese) {
                            Add-Hit $rel $lineNo $line "C# .$t 中文字面量"
                        }
                    }
                }
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
