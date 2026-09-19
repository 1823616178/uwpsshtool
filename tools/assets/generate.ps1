# Q07：由 tools/assets/icon.svg 生成 src/SshTool.App/Assets 下的全套磁贴/图标/启动画面资源。
# 不依赖外部工具：用 WPF 渲染（PresentationCore），只认我们自己写的 SVG 子集（rect / path，纯色填充与描边）。
#
#   pwsh tools/assets/generate.ps1            # 生成全部
#   pwsh tools/assets/generate.ps1 -List      # 只列将生成的文件
#
# 命名遵循 UWP 资源限定符：<基名>.scale-200.png、<基名>.targetsize-32_altform-unplated.png，
# 清单里只写不带限定符的基名，系统按屏幕缩放与场景自动挑。
[CmdletBinding()]
param([switch]$List)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$OutDir = Join-Path $RepoRoot 'src\SshTool.App\Assets'
$IconPath = Join-Path $PSScriptRoot 'icon.svg'
$PlatedPath = Join-Path $PSScriptRoot 'icon-plated.svg'

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

# ---- 极小 SVG 子集解析：只处理 rect 与 path，纯色 fill / stroke ----
function Get-SvgShapes([string]$path) {
    [xml]$doc = Get-Content $path -Raw
    $svg = $doc.svg
    $viewBox = ($svg.viewBox -split '\s+') | ForEach-Object { [double]$_ }
    $shapes = @()
    foreach ($node in $svg.ChildNodes) {
        if ($node.NodeType -ne 'Element') { continue }
        switch ($node.LocalName) {
            'rect' {
                $rx = if ($node.rx) { [double]$node.rx } else { 0 }
                $shapes += [pscustomobject]@{
                    Kind = 'rect'
                    X = [double]$node.x; Y = [double]$node.y
                    W = [double]$node.width; H = [double]$node.height; R = $rx
                    Fill = $node.fill
                }
            }
            'path' {
                $shapes += [pscustomobject]@{
                    Kind = 'path'
                    Data = $node.d
                    Fill = $node.fill
                    Stroke = $node.stroke
                    StrokeWidth = [double]$node.GetAttribute('stroke-width')
                    Cap = $node.GetAttribute('stroke-linecap')
                }
            }
        }
    }
    return [pscustomobject]@{ Width = $viewBox[2]; Height = $viewBox[3]; Shapes = $shapes }
}

function ConvertTo-Brush([string]$color) {
    if (-not $color -or $color -eq 'none') { return $null }
    return New-Object System.Windows.Media.SolidColorBrush(
        [System.Windows.Media.ColorConverter]::ConvertFromString($color))
}

# 把图形按 scale 画进 DrawingContext，整体平移到 (offsetX, offsetY)
function Add-Shapes($dc, $svg, [double]$scale, [double]$offsetX, [double]$offsetY) {
    $dc.PushTransform((New-Object System.Windows.Media.TranslateTransform($offsetX, $offsetY)))
    $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform($scale, $scale)))
    foreach ($s in $svg.Shapes) {
        $fill = ConvertTo-Brush $s.Fill
        if ($s.Kind -eq 'rect') {
            $rect = New-Object System.Windows.Rect($s.X, $s.Y, $s.W, $s.H)
            $dc.DrawRoundedRectangle($fill, $null, $rect, $s.R, $s.R)
        }
        else {
            $geometry = [System.Windows.Media.Geometry]::Parse($s.Data)
            $pen = $null
            if ($s.Stroke -and $s.Stroke -ne 'none') {
                $pen = New-Object System.Windows.Media.Pen((ConvertTo-Brush $s.Stroke), $s.StrokeWidth)
                if ($s.Cap -eq 'round') {
                    $pen.StartLineCap = 'Round'; $pen.EndLineCap = 'Round'; $pen.LineJoin = 'Round'
                }
            }
            $dc.DrawGeometry($fill, $pen, $geometry)
        }
    }
    $dc.Pop(); $dc.Pop()
}

# 渲染一张 PNG：字形等比缩放到画布的 CoverRatio，居中
function New-AssetPng($svg, [int]$width, [int]$height, [double]$coverRatio, [string]$file) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $scale = [Math]::Min($width / $svg.Width, $height / $svg.Height) * $coverRatio
    $offsetX = ($width - $svg.Width * $scale) / 2
    $offsetY = ($height - $svg.Height * $scale) / 2
    Add-Shapes $dc $svg $scale $offsetX $offsetY
    $dc.Close()

    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap(
        $width, $height, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [System.IO.File]::Create($file)
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
}

# ---- 资源清单：基名 → 逻辑尺寸 + 字形占比 ----
$Scales = @(100, 125, 150, 200, 400)
$Assets = @(
    @{ Name = 'Square44x44Logo';   W = 44;  H = 44;  Cover = 0.72; Plated = $false }
    @{ Name = 'Square71x71Logo';   W = 71;  H = 71;  Cover = 0.62; Plated = $false }
    @{ Name = 'Square150x150Logo'; W = 150; H = 150; Cover = 0.55; Plated = $false }
    @{ Name = 'Square310x310Logo'; W = 310; H = 310; Cover = 0.50; Plated = $false }
    @{ Name = 'Wide310x150Logo';   W = 310; H = 150; Cover = 0.55; Plated = $false }
    @{ Name = 'StoreLogo';         W = 50;  H = 50;  Cover = 1.00; Plated = $true }
    @{ Name = 'SplashScreen';      W = 620; H = 300; Cover = 0.42; Plated = $false }
)
# 应用列表/搜索结果用的小尺寸；unplated 是无底板变体（深色开始屏幕下不带方块）
$TargetSizes = @(16, 24, 32, 48, 256)

if ($List) {
    # 每个基名产出 Scales.Count 个 scale 文件 + scale-100 时附带 1 份无限定符基名（供清单引用）
    $count = $Assets.Count * ($Scales.Count + 1) + $TargetSizes.Count * 2
    Write-Host "将生成 $count 个 PNG 到 $OutDir"
    exit 0
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
$glyph = Get-SvgShapes $IconPath
$plated = Get-SvgShapes $PlatedPath
$made = 0

foreach ($a in $Assets) {
    $svg = if ($a.Plated) { $plated } else { $glyph }
    foreach ($scale in $Scales) {
        # AwayFromZero：.NET 默认是银行家舍入，50×125% = 62.5 会被舍成 62，
        # 而 UWP 校验 StoreLogo.scale-125 必须是 63×63（APPX1619）。
        $w = [int][Math]::Round($a.W * $scale / 100, [MidpointRounding]::AwayFromZero)
        $h = [int][Math]::Round($a.H * $scale / 100, [MidpointRounding]::AwayFromZero)
        $suffix = if ($scale -eq 100) { '' } else { ".scale-$scale" }
        # scale-100 也写带限定符的一份，命名统一；不带限定符的基名单独再写一份供清单引用
        New-AssetPng $svg $w $h $a.Cover (Join-Path $OutDir "$($a.Name).scale-$scale.png")
        $made++
        if ($scale -eq 100) {
            New-AssetPng $svg $w $h $a.Cover (Join-Path $OutDir "$($a.Name).png")
            $made++
        }
    }
}

foreach ($size in $TargetSizes) {
    New-AssetPng $plated $size $size 1.00 (Join-Path $OutDir "Square44x44Logo.targetsize-$size.png")
    New-AssetPng $glyph  $size $size 0.86 (Join-Path $OutDir "Square44x44Logo.targetsize-${size}_altform-unplated.png")
    $made += 2
}

Write-Host "OK：生成 $made 个 PNG → $OutDir"
exit 0
