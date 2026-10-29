# 从本项目 SVG 矢量母版导出全部 MSIX、任务栏和托盘图标；不使用旧项目资源。
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore,PresentationFramework,WindowsBase
$assetRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\HybridMount\Assets'))
[xml]$svg = Get-Content -LiteralPath (Join-Path $assetRoot 'HybridMount.svg') -Raw
function Get-Brush([string]$color) {
    if ($color -eq 'none') { return $null }
    if ($color -eq 'url(#tile)') {
        return [Windows.Media.LinearGradientBrush]::new([Windows.Media.ColorConverter]::ConvertFromString('#228BD0'), [Windows.Media.ColorConverter]::ConvertFromString('#2451AA'), 45)
    }
    return [Windows.Media.SolidColorBrush]::new([Windows.Media.ColorConverter]::ConvertFromString($color))
}
function New-LogoPng([string]$name, [double]$width, [double]$height, [double]$logoSize) {
    # MSIX 的分数缩放尺寸向上取整，例如 50px 的 125% 为 63px。
    $width = [int][Math]::Ceiling($width)
    $height = [int][Math]::Ceiling($height)
    $visual = [Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    $drawing.PushTransform([Windows.Media.TranslateTransform]::new(($width-$logoSize)/2, ($height-$logoSize)/2))
    $drawing.PushTransform([Windows.Media.ScaleTransform]::new($logoSize/256, $logoSize/256))
    foreach ($node in $svg.DocumentElement.ChildNodes) {
        if ($node.LocalName -eq 'defs') { continue }
        $brush = Get-Brush $node.GetAttribute('fill')
        $pen = $null
        if ($node.HasAttribute('stroke')) {
            $pen = [Windows.Media.Pen]::new((Get-Brush $node.GetAttribute('stroke')), [double]$node.GetAttribute('stroke-width'))
            $pen.StartLineCap = 'Round'; $pen.EndLineCap = 'Round'
        }
        switch ($node.LocalName) {
            'rect' { $drawing.DrawRoundedRectangle($brush,$pen,[Windows.Rect]::new([double]$node.x,[double]$node.y,[double]$node.width,[double]$node.height),[double]$node.rx,[double]$node.rx) }
            'path' { $drawing.DrawGeometry($brush,$pen,[Windows.Media.Geometry]::Parse($node.d)) }
            'circle' { $drawing.DrawEllipse($brush,$pen,[Windows.Point]::new([double]$node.cx,[double]$node.cy),[double]$node.r,[double]$node.r) }
            'ellipse' { $drawing.DrawEllipse($brush,$pen,[Windows.Point]::new([double]$node.cx,[double]$node.cy),[double]$node.rx,[double]$node.ry) }
        }
    }
    $drawing.Pop(); $drawing.Pop(); $drawing.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($width,$height,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.File]::Create((Join-Path $assetRoot $name))
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
}
foreach ($scale in @(100,125,150,200,400)) {
    New-LogoPng "HybridMountSquare44.scale-$scale.png" (44*$scale/100) (44*$scale/100) (44*$scale/100)
    New-LogoPng "HybridMountSquare150.scale-$scale.png" (150*$scale/100) (150*$scale/100) (112*$scale/100)
    New-LogoPng "HybridMountWide.scale-$scale.png" (310*$scale/100) (150*$scale/100) (112*$scale/100)
    New-LogoPng "HybridMountSplash.scale-$scale.png" (620*$scale/100) (300*$scale/100) (160*$scale/100)
    New-LogoPng "HybridMountStore.scale-$scale.png" (50*$scale/100) (50*$scale/100) (50*$scale/100)
}
$sizes = @(16,20,24,32,40,44,48,64,96,128,256)
foreach ($size in $sizes) {
    New-LogoPng "HybridMountSquare44.targetsize-${size}_altform-unplated.png" $size $size $size
}
New-LogoPng 'HybridMount.png' 256 256 256
# ICO 每一帧使用相同矢量图形，保留真实 alpha，覆盖托盘到高 DPI 快捷方式。
$iconPath = Join-Path $assetRoot 'HybridMount.ico'
$iconStream = [IO.File]::Create($iconPath)
$writer = [IO.BinaryWriter]::new($iconStream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16*$sizes.Count
    foreach ($size in $sizes) {
        $png = [IO.File]::ReadAllBytes((Join-Path $assetRoot "HybridMountSquare44.targetsize-${size}_altform-unplated.png"))
        $dimension = if ($size -eq 256) { 0 } else { $size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$png.Length); $writer.Write([uint32]$offset)
        $offset += $png.Length
    }
    foreach ($size in $sizes) { $writer.Write([IO.File]::ReadAllBytes((Join-Path $assetRoot "HybridMountSquare44.targetsize-${size}_altform-unplated.png"))) }
} finally { $writer.Dispose(); $iconStream.Dispose() }
Write-Host 'HybridMount 骡子图标和 MSIX 资源已重新生成。'
