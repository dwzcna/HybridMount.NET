# 检查实际交付包，不把成功编译当作打包内容正确的证据。
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][ValidateSet('x64','ARM64')][string]$Platform,[switch]$ExpectCloudThumbnails)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($Path))
try {
    $entries = @{}
    foreach ($entry in $archive.Entries) { $entries[$entry.FullName.Replace('\','/')] = $entry }
    foreach ($required in @('AppxManifest.xml','resources.pri','HybridMount.NET.exe','HybridMount.NET.dll','HybridMount.NET.runtimeconfig.json','HybridMount.Ext.dll','coreclr.dll','AWSSDK.Core.dll','AWSSDK.S3.dll','WebDAVClient.dll','AWSSDK.SQS.dll','ThirdParty/WebDAVClient/LICENSE.txt','ThirdParty/WebDAVClient/NOTICE.txt','ThirdParty/AWSSDK/License.txt','ThirdParty/AWSSDK/Notice.txt','ThirdParty/AWSSDK/SqsPackage.txt','Assets/HybridMount.ico','Assets/HybridMount.png')) {
        if (-not $entries.ContainsKey($required)) { throw "包缺少 $required" }
    }
    function Read-Bytes([string]$entryName) {
        $stream = $entries[$entryName].Open(); $buffer = [IO.MemoryStream]::new()
        try { $stream.CopyTo($buffer); return ,$buffer.ToArray() } finally { $stream.Dispose(); $buffer.Dispose() }
    }
    [xml]$manifest = [Text.Encoding]::UTF8.GetString((Read-Bytes 'AppxManifest.xml')).TrimStart([char]0xfeff)
    [xml]$props = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\Directory.Build.props') -Raw
    $version = [string]$props.Project.PropertyGroup[0].Version
    $binaryVersion=if(([version]$version).Revision -lt 0) { "$version.0" } else { $version }
    if ($manifest.Package.Identity.Version -ne $binaryVersion -or $manifest.Package.Identity.Name -ne 'dwzcn.HybridMount.NET') { throw 'MSIX 产品身份或版本不一致。' }
    if ($manifest.Package.Identity.ProcessorArchitecture -ne $Platform.ToLowerInvariant()) { throw '包声明架构不正确。' }
    $expectedMachine = if ($Platform -eq 'ARM64') { 0xaa64 } else { 0x8664 }
    foreach ($entryName in @('HybridMount.NET.exe','HybridMount.Ext.dll','coreclr.dll')) {
        $bytes = Read-Bytes $entryName
        $pe = [BitConverter]::ToInt32($bytes,0x3c)
        if ([BitConverter]::ToUInt16($bytes,$pe+4) -ne $expectedMachine) { throw "$entryName 架构不正确。" }
    }
    # 检查各缩放图的 PNG 头尺寸，防止旧资源或错误舍入混入发布包。
    foreach ($spec in @(@('HybridMountSquare44',44,44),@('HybridMountSquare150',150,150),@('HybridMountWide',310,150),@('HybridMountSplash',620,300),@('HybridMountStore',50,50))) {
        foreach ($scale in @(100,125,150,200,400)) {
            $entryName = "Assets/$($spec[0]).scale-$scale.png"
            if (-not $entries.ContainsKey($entryName)) { throw "缺少图形 $entryName" }
            $png = Read-Bytes $entryName
            $width = [uint32]$png[16]*16777216 + [uint32]$png[17]*65536 + [uint32]$png[18]*256 + $png[19]
            $height = [uint32]$png[20]*16777216 + [uint32]$png[21]*65536 + [uint32]$png[22]*256 + $png[23]
            if ($width -ne [Math]::Ceiling($spec[1]*$scale/100) -or $height -ne [Math]::Ceiling($spec[2]*$scale/100)) { throw "图形尺寸错误：$entryName" }
        }
    }
    $runtime = [Text.Encoding]::UTF8.GetString((Read-Bytes 'HybridMount.NET.runtimeconfig.json')) | ConvertFrom-Json
    if ($runtime.runtimeOptions.framework -or $runtime.runtimeOptions.frameworks) { throw '主程序仍依赖共享 .NET 运行时。' }
    if ($entries.Keys | Where-Object { $_ -match '(?i)EPaperKit|ProKit|Square44x44Logo|StoreLogo' }) { throw '包内出现旧产品资源或组件。' }
    $text = $manifest.OuterXml
    foreach ($registration in @('windows.cloudFiles','CloudFiles','windows.startupTask','windows.comServer','windows.fileExplorerContextMenus','HybridMountStartup','HybridMount.Ext.dll')) {
        if (-not $text.Contains($registration)) { throw "清单缺少 $registration" }
    }
    # 基础状态与属性处理器始终注册；缩略图仍独立门控，核验增量构建未遗留或重复。
    foreach($handler in @('CustomStateHandler','ExtendedPropertyHandler','BannersHandler','CloudFilesContextMenus')){
        if($manifest.SelectNodes("//*[local-name()='CloudFiles']/*[local-name()='$handler']").Count -ne 1){throw "基础云文件处理器缺失或重复：$handler"}
    }
    if($manifest.SelectNodes("//*[local-name()='CloudFiles']/*[local-name()='ThumbnailProviderHandler']").Count -gt 1){throw '云缩略图处理器重复。'}
    $thumbnail=$manifest.SelectSingleNode("//*[local-name()='CloudFiles']/*[local-name()='ThumbnailProviderHandler']")
    if($null -eq $thumbnail){throw 'SDK要求的系统缩略图节点缺失。'}
    $customThumbnail=$thumbnail.HasAttribute('Clsid')
    if([bool]$customThumbnail -ne $ExpectCloudThumbnails.IsPresent){throw '自定义云缩略图注册与本次构建的启用门槛不一致。'}
    if($customThumbnail -and $thumbnail.Clsid -ne 'A65802EB-4D1C-4C7E-A519-34C76B9BB281'){throw '云缩略图 CLSID 不正确。'}
    if(!$text.Contains('A65802EB-4D1C-4C7E-A519-34C76B9BB281')){throw '包缺少候选缩略图 COM 类。'}
    Write-Host "包验证通过：$Platform / $version / 自包含 / 原生扩展 / 全新资源 / CloudFiles、启动与菜单声明。"
} finally { $archive.Dispose() }
