# 检查实际交付包，不把成功编译当作打包内容正确的证据。
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][ValidateSet('x64','ARM64')][string]$Platform,[switch]$ExpectCloudThumbnails)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($Path))
try {
    $entries = @{}
    foreach ($entry in $archive.Entries) { $entries[$entry.FullName.Replace('\','/')] = $entry }
    foreach ($required in @('AppxManifest.xml','resources.pri','HybridMount.NET.exe','HybridMount.NET.dll','HybridMount.NET.runtimeconfig.json','HybridMount.Ext.dll','coreclr.dll','AWSSDK.Core.dll','AWSSDK.S3.dll','WebDAVClient.dll','AWSSDK.SQS.dll','ThirdParty/WebDAVClient/LICENSE.txt','ThirdParty/WebDAVClient/NOTICE.txt','ThirdParty/AWSSDK/License.txt','ThirdParty/AWSSDK/Notice.txt','ThirdParty/AWSSDK/SqsPackage.txt','Assets/HybridMount.ico','Assets/HybridMount.png','Assets/SyncIssue.png','Assets/SyncExcluded.png','Assets/SyncExcluded.ico','Assets/SyncIssue.ico')) {
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
    # 主入口保持既有虚拟化；隐藏维护入口必须独立使用经隔离验证的win32App／mediumIL组合。
    $apps=$manifest.SelectNodes("//*[local-name()='Applications']/*[local-name()='Application']")
    $main=@($apps|Where-Object {$_.Id -eq 'App'})
    $maintenance=@($apps|Where-Object {$_.Id -eq 'ShellMaintenance'})
    if($apps.Count -ne 2 -or $main.Count -ne 1 -or $maintenance.Count -ne 1){throw '应用及隐藏维护入口缺失或重复。'}
    $runtimeNamespace='http://schemas.microsoft.com/appx/manifest/uap/windows10/10'
    $entry=$maintenance[0]
    if($main[0].EntryPoint -ne 'Windows.FullTrustApplication' -or $entry.HasAttribute('EntryPoint') -or
       $entry.Executable -ne 'HybridMount.NET.exe' -or $entry.GetAttribute('RuntimeBehavior',$runtimeNamespace) -ne 'win32App' -or
       $entry.GetAttribute('TrustLevel',$runtimeNamespace) -ne 'mediumIL' -or $entry.VisualElements.AppListEntry -ne 'none'){
        throw '隐藏维护入口运行行为不正确，不能据此进行真实Shell清理。'
    }
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
    foreach ($registration in @('windows.startupTask','HybridMountStartup','ShellMaintenance')) {
        if (-not $text.Contains($registration)) { throw "清单缺少 $registration" }
    }
    # 实测否定当前候选，正式包必须保留空CloudFiles及唯一原生状态路径。
    if($ExpectCloudThumbnails){throw '0.0.9.37缩略图自动绑定尚未通过兼容门槛，不能验收为启用。'}
    foreach($category in @('windows.fileExplorerContextMenus','windows.fileTypeAssociation','windows.comServer')){if($manifest.SelectNodes("//*[local-name()='Extension' and @Category='$category']").Count){throw "正式包意外注册 $category"}}
    foreach($name in @('CloudFilesContextMenus','CustomStateHandler','ExtendedPropertyHandler','BannersHandler','ThumbnailProviderHandler')){if($manifest.SelectNodes("//*[local-name()='$name']").Count){throw "正式包含未通过验证的云处理器：$name"}}
    $cloud=$manifest.SelectNodes("//*[local-name()='CloudFiles']");if($cloud.Count -ne 1 -or $cloud[0].IconResource -ne 'Assets\HybridMount.png' -or $cloud[0].ChildNodes.Count){throw 'CloudFiles必须仅含提供程序图标。'}
    $excluded=$manifest.SelectNodes("//*[local-name()='ExcludedDirectory']")
    if($excluded.Count -ne 1 -or $excluded[0].InnerText -ne '$(KnownFolder:LocalAppData)\HybridMount.NET\Mounting\Thumbnails'){throw '共享缩略图目录的精确虚拟化排除不符。'}
    if(-not $manifest.SelectNodes("//*[local-name()='Capability' and @Name='unvirtualizedResources']").Count){throw '缺少共享缓存所需的受限能力。'}
    Write-Host "包验证通过：$Platform / $version / 自包含 / 数据库缩略图自动绑定未完成、注册门控 / 自有菜单暂停 / 原生状态及维护入口保留。"

} finally { $archive.Dispose() }
