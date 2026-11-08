# 从 SVG 母版导出应用、托盘和 MSIX 图形；仅依赖 Windows WPF，不访问网络。
[CmdletBinding()]
param([switch]$StatusIconsOnly)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationCore,PresentationFramework,WindowsBase
$assetRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\HybridMount\Assets'))
$settings=[Xml.XmlReaderSettings]::new();$settings.DtdProcessing='Prohibit';$settings.XmlResolver=$null
$reader=[Xml.XmlReader]::Create((Join-Path $assetRoot 'HybridMount.svg'),$settings)
$svg=[Xml.XmlDocument]::new();$svg.XmlResolver=$null
try{$svg.Load($reader)}finally{$reader.Dispose()}
$definitions=@{};foreach($node in $svg.SelectNodes('//*[local-name()="defs"]/*[@id]')){$definitions[$node.GetAttribute('id')]=$node}
function Number([string]$value,[double]$fallback=0){if([string]::IsNullOrWhiteSpace($value)){return $fallback};return [double]::Parse($value,[Globalization.CultureInfo]::InvariantCulture)}
function Coordinate([string]$value,[double]$fallback){if($value.EndsWith('%')){return (Number $value.TrimEnd('%'))/100};return Number $value $fallback}
function Node-Transform($node){
    $value=$node.GetAttribute('transform');$group=[Windows.Media.TransformGroup]::new()
    if([string]::IsNullOrWhiteSpace($value)){return $group}
    $matches=[regex]::Matches($value,'([A-Za-z]+)\s*\(([^)]*)\)')
    if($matches.Count -eq 0 -or [regex]::Replace($value,'([A-Za-z]+)\s*\(([^)]*)\)','').Trim(' ',',',"`r","`n").Length -ne 0){throw 'SVG 变换格式无效。'}
    # WPF 变换组与 SVG 列表的矩阵应用顺序相反。
    for($index=$matches.Count-1;$index -ge 0;$index--){
        $match=$matches[$index];$values=@([regex]::Matches($match.Groups[2].Value,'[-+]?(?:\d*\.\d+|\d+\.?\d*)(?:[eE][-+]?\d+)?')|ForEach-Object {Number $_.Value})
        $transform=switch -CaseSensitive ($match.Groups[1].Value){
            'translate' {if($values.Count -notin @(1,2)){throw 'translate 参数无效。'};[Windows.Media.TranslateTransform]::new($values[0],$(if($values.Count -eq 2){$values[1]}else{0}))}
            'scale' {if($values.Count -notin @(1,2)){throw 'scale 参数无效。'};[Windows.Media.ScaleTransform]::new($values[0],$(if($values.Count -eq 2){$values[1]}else{$values[0]}))}
            'rotate' {if($values.Count -notin @(1,3)){throw 'rotate 参数无效。'};[Windows.Media.RotateTransform]::new($values[0],$(if($values.Count -eq 3){$values[1]}else{0}),$(if($values.Count -eq 3){$values[2]}else{0}))}
            'skewX' {[Windows.Media.SkewTransform]::new($values[0],0)}
            'skewY' {[Windows.Media.SkewTransform]::new(0,$values[0])}
            'matrix' {if($values.Count -ne 6){throw 'matrix 参数无效。'};[Windows.Media.MatrixTransform]::new([Windows.Media.Matrix]::new($values[0],$values[1],$values[2],$values[3],$values[4],$values[5]))}
            default {throw ('不支持的 SVG 变换：'+$match.Groups[1].Value)}
        };$group.Children.Add($transform)
    };return $group
}
function Paint([string]$value,[double]$opacity=1){
    if($value -eq 'none'){return $null}
    if($value -match '^url\(#([^)]+)\)$'){
        $definition=$definitions[$Matches[1]];if($null -eq $definition){throw ('缺少 SVG 渐变：'+$value)}
        switch($definition.LocalName){
            'linearGradient' {$brush=[Windows.Media.LinearGradientBrush]::new();$brush.StartPoint=[Windows.Point]::new((Coordinate $definition.GetAttribute('x1') 0),(Coordinate $definition.GetAttribute('y1') 0));$brush.EndPoint=[Windows.Point]::new((Coordinate $definition.GetAttribute('x2') 1),(Coordinate $definition.GetAttribute('y2') 0))}
            'radialGradient' {$brush=[Windows.Media.RadialGradientBrush]::new();$cx=Coordinate $definition.GetAttribute('cx') .5;$cy=Coordinate $definition.GetAttribute('cy') .5;$brush.Center=[Windows.Point]::new($cx,$cy);$brush.GradientOrigin=[Windows.Point]::new((Coordinate $definition.GetAttribute('fx') $cx),(Coordinate $definition.GetAttribute('fy') $cy));$brush.RadiusX=Coordinate $definition.GetAttribute('r') .5;$brush.RadiusY=$brush.RadiusX}
            default {throw ('不支持的 SVG 画刷：'+$definition.LocalName)}
        }
        if($definition.GetAttribute('gradientUnits') -eq 'userSpaceOnUse'){$brush.MappingMode='Absolute'}
        foreach($stop in $definition.ChildNodes){if($stop.NodeType -ne 'Element'){continue};if($stop.LocalName -ne 'stop'){throw '渐变包含未支持节点。'};$color=[Windows.Media.ColorConverter]::ConvertFromString($stop.GetAttribute('stop-color'));$color.A=[byte][Math]::Round($color.A*(Number $stop.GetAttribute('stop-opacity') 1));$brush.GradientStops.Add([Windows.Media.GradientStop]::new($color,(Coordinate $stop.GetAttribute('offset') 0)))}
        if($definition.HasAttribute('gradientTransform')){throw '暂不支持 gradientTransform，请先扩展导出器。'}
    }else{$brush=[Windows.Media.SolidColorBrush]::new([Windows.Media.ColorConverter]::ConvertFromString($value))}
    $brush.Opacity=$opacity;return $brush
}
function Geometry($node,[string]$rule='nonzero'){
    $geometry=switch($node.LocalName){
        'path' {$path=[Windows.Media.PathGeometry]::CreateFromGeometry([Windows.Media.Geometry]::Parse($node.GetAttribute('d')));$path.FillRule=$(if($rule -eq 'evenodd'){'EvenOdd'}else{'Nonzero'});$path}
        'rect' {$rx=Number $node.GetAttribute('rx');$ry=Number $node.GetAttribute('ry') $rx;[Windows.Media.RectangleGeometry]::new([Windows.Rect]::new((Number $node.GetAttribute('x')),(Number $node.GetAttribute('y')),(Number $node.GetAttribute('width')),(Number $node.GetAttribute('height'))),$rx,$ry)}
        'circle' {$radius=Number $node.GetAttribute('r');[Windows.Media.EllipseGeometry]::new([Windows.Point]::new((Number $node.GetAttribute('cx')),(Number $node.GetAttribute('cy'))),$radius,$radius)}
        'ellipse' {[Windows.Media.EllipseGeometry]::new([Windows.Point]::new((Number $node.GetAttribute('cx')),(Number $node.GetAttribute('cy'))),(Number $node.GetAttribute('rx')),(Number $node.GetAttribute('ry')))}
        default {throw ('不支持的 SVG 图形：'+$node.LocalName)}
    };return $geometry
}
function Draw-Node($drawing,$node,[hashtable]$parent){
    if($node.NodeType -ne 'Element' -or $node.LocalName -in @('defs','title','desc')){return}
    $style=$parent.Clone()
    foreach($name in @('fill','stroke','stroke-width','stroke-linecap','stroke-linejoin','fill-rule','fill-opacity','stroke-opacity')){if($node.HasAttribute($name)){$style[$name]=$node.GetAttribute($name)}}
    if($node.HasAttribute('style')){foreach($part in $node.GetAttribute('style').Split(';')){if($part.Trim().Length -eq 0){continue};$pair=$part.Split(':',2);if($pair.Count -ne 2 -or -not $style.ContainsKey($pair[0].Trim())){throw ('不支持的 SVG 样式：'+$part)};$style[$pair[0].Trim()]=$pair[1].Trim()}}
    $pushes=0
    try{
        if($node.HasAttribute('transform')){$drawing.PushTransform((Node-Transform $node));$pushes++}
        if($node.HasAttribute('opacity')){$drawing.PushOpacity((Number $node.GetAttribute('opacity') 1));$pushes++}
        if($node.HasAttribute('clip-path')){
            if($node.GetAttribute('clip-path') -notmatch '^url\(#([^)]+)\)$'){throw '不支持外部 SVG 裁剪。'}
            $clip=$definitions[$Matches[1]];if($null -eq $clip -or $clip.LocalName -ne 'clipPath' -or $clip.GetAttribute('clipPathUnits') -notin @('','userSpaceOnUse')){throw 'SVG 裁剪坐标无效。'}
            $combined=[Windows.Media.GeometryGroup]::new();$combined.FillRule='Nonzero'
            foreach($child in $clip.ChildNodes){if($child.NodeType -ne 'Element'){continue};$shape=Geometry $child;if($child.HasAttribute('transform')){$shape.Transform=Node-Transform $child};$combined.Children.Add($shape)}
            $drawing.PushClip($combined);$pushes++
        }
        if($node.LocalName -eq 'g'){foreach($child in $node.ChildNodes){Draw-Node $drawing $child $style};return}
        $fill=Paint $style['fill'] (Number $style['fill-opacity'] 1);$stroke=Paint $style['stroke'] (Number $style['stroke-opacity'] 1);$pen=$null
        if($null -ne $stroke){$pen=[Windows.Media.Pen]::new($stroke,(Number $style['stroke-width'] 1));$pen.StartLineCap=$pen.EndLineCap=switch($style['stroke-linecap']){'round'{'Round'};'square'{'Square'};default{'Flat'}};$pen.LineJoin=switch($style['stroke-linejoin']){'round'{'Round'};'bevel'{'Bevel'};default{'Miter'}}}
        $drawing.DrawGeometry($fill,$pen,(Geometry $node $style['fill-rule']))
    }finally{while($pushes-- -gt 0){$drawing.Pop()}}
}
function New-LogoPng([string]$name,[double]$width,[double]$height,[double]$logoSize,[switch]$AsBytes){
    # 非整数缩放向上取整；使用 viewBox 保持原图形比例及透明留白。
    $width=[int][Math]::Ceiling($width);$height=[int][Math]::Ceiling($height)
    $view=@($svg.DocumentElement.GetAttribute('viewBox') -split '[,\s]+'|Where-Object {$_}|ForEach-Object {Number $_})
    if($view.Count -ne 4 -or $view[2] -le 0 -or $view[3] -le 0){throw 'SVG viewBox 无效。'}
    $scale=$logoSize/[Math]::Max($view[2],$view[3]);$visual=[Windows.Media.DrawingVisual]::new();$drawing=$visual.RenderOpen()
    try{
        $drawing.PushTransform([Windows.Media.TranslateTransform]::new(($width-$view[2]*$scale)/2,($height-$view[3]*$scale)/2))
        $drawing.PushTransform([Windows.Media.ScaleTransform]::new($scale,$scale));$drawing.PushTransform([Windows.Media.TranslateTransform]::new(-$view[0],-$view[1]))
        $style=@{'fill'='#000000';'stroke'='none';'stroke-width'='1';'stroke-linecap'='butt';'stroke-linejoin'='miter';'fill-rule'='nonzero';'fill-opacity'='1';'stroke-opacity'='1'}
        foreach($node in $svg.DocumentElement.ChildNodes){Draw-Node $drawing $node $style}
        $drawing.Pop();$drawing.Pop();$drawing.Pop()
    }finally{$drawing.Close()}
    $bitmap=[Windows.Media.Imaging.RenderTargetBitmap]::new($width,$height,96,96,[Windows.Media.PixelFormats]::Pbgra32);$bitmap.Render($visual)
    $encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new();$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap));$stream=if($AsBytes){[IO.MemoryStream]::new()}else{[IO.File]::Create((Join-Path $assetRoot $name))}
    try{$encoder.Save($stream);if($AsBytes){return ,$stream.ToArray()}}finally{$stream.Dispose()}
}

# 每帧独立从SVG渲染，ICO目录保留真实尺寸和alpha；系统按DPI选图，不将PNG路径当图标资源。
function Write-Icon([string]$path,[object[]]$frames){
    $stream=[IO.File]::Create($path);$writer=[IO.BinaryWriter]::new($stream)
    try{
        $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$frames.Count);$offset=6+16*$frames.Count
        foreach($frame in $frames){
            $dimension=if($frame.Size -eq 256){0}else{$frame.Size}
            $writer.Write([byte]$dimension);$writer.Write([byte]$dimension);$writer.Write([byte]0);$writer.Write([byte]0)
            $writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$frame.Bytes.Length);$writer.Write([uint32]$offset);$offset+=$frame.Bytes.Length
        }
        foreach($frame in $frames){$writer.Write([byte[]]$frame.Bytes)}
    }finally{$writer.Dispose();$stream.Dispose()}
}
$applicationSvg=$svg
foreach($name in @('SyncExcluded','SyncIssue')){
    $statusReader=[Xml.XmlReader]::Create((Join-Path $assetRoot ($name+'.svg')),$settings)
    $svg=[Xml.XmlDocument]::new();$svg.XmlResolver=$null
    try{$svg.Load($statusReader)}finally{$statusReader.Dispose()}
    New-LogoPng ($name+'.png') 32 32 32
    $frames=@(foreach($size in @(16,20,24,32,40,48,64,128,256)){
        [pscustomobject]@{Size=$size;Bytes=(New-LogoPng '' $size $size $size -AsBytes)}
    })
    Write-Icon (Join-Path $assetRoot ($name+'.ico')) $frames
}
$svg=$applicationSvg
if($StatusIconsOnly){Write-Host '排除及同步问题多尺寸状态图标已重新生成。';return}
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
# 应用ICO继续复用同一导出器，保留托盘与高DPI所需帧。
$frames=@(foreach($size in $sizes){[pscustomobject]@{Size=$size;Bytes=[IO.File]::ReadAllBytes((Join-Path $assetRoot "HybridMountSquare44.targetsize-${size}_altform-unplated.png"))}})
Write-Icon (Join-Path $assetRoot 'HybridMount.ico') $frames
Write-Host 'HybridMount 骡子图标和 MSIX 资源已重新生成。'
