# 检查实际交付包，不把成功编译当作打包内容正确的证据。
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][ValidateSet('x64','ARM64')][string]$Platform,[switch]$ExpectCloudThumbnails,[ValidateSet('Native','Menus','MenusAndThumbnails')][string]$ShellProfile='Menus')
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
    # 校验实际选择的唯一注册组合，不允许额外文件类型接管或未实现处理器CLSID。
    if($ExpectCloudThumbnails -and $ShellProfile -ne 'MenusAndThumbnails'){throw '缩略图预期与Shell配置不一致。'}
    if($manifest.SelectNodes("//*[local-name()='Extension' and @Category='windows.fileTypeAssociation']").Count){throw '不得接管全局文件类型。'}
    $cloud=$manifest.SelectNodes("//*[local-name()='CloudFiles']");if($cloud.Count -ne 1 -or $cloud[0].IconResource -ne 'Assets\HybridMount.png'){throw '云提供程序声明不正确。'}
    $classes=@($manifest.SelectNodes("//*[local-name()='ComServer']//*[local-name()='Class']"));$verbs=@($manifest.SelectNodes("//*[local-name()='Verb']"));$menu='F4A8FD0B-4055-4F23-8CC0-91B736B3E491';$thumb='A65802EB-4D1C-4C7E-A519-34C76B9BB281'
    $expected=if($ShellProfile -eq 'Native'){@()}elseif($ShellProfile -eq 'Menus'){@($menu)}else{@($menu,$thumb)}
    if($classes.Count -ne $expected.Count -or @($classes|Where-Object {$_.Id -notin $expected}).Count){throw 'COM类与所选配置不一致。'}
    foreach($class in $classes){if($class.ParentNode.LocalName -ne 'ExeServer' -or $class.ParentNode.Executable -ne 'HybridMount.NET.exe' -or $class.ParentNode.Arguments -ne '--shell-server'){throw 'Shell类没有绑定独立早期宿主。'}}
    if($ShellProfile -eq 'Native'){if($cloud[0].ChildNodes.Count -or $verbs.Count){throw '原生配置意外含扩展。'}}
    elseif($ShellProfile -eq 'Menus'){if($cloud[0].ChildNodes.Count -or $verbs.Count -ne 3 -or @($verbs|Where-Object {$_.Clsid -ne $menu}).Count){throw '独立动态菜单注册不符。'}}
    else{
        if($cloud[0].ChildNodes.Count -ne 5 -or $verbs.Count -ne 1 -or $verbs[0].Clsid -ne $menu){throw '云菜单和缩略图组合不完整或重复。'}
        $handler=$cloud[0].SelectSingleNode("*[local-name()='ThumbnailProviderHandler']");if(!$handler -or $handler.Clsid -ne $thumb){throw '云缩略图入口不符。'}
        foreach($name in @('CustomStateHandler','ExtendedPropertyHandler','BannersHandler')){$node=$cloud[0].SelectSingleNode("*[local-name()='$name']");if(!$node -or $node.HasAttribute('Clsid')){throw '不得注册未实现的云处理器。'}}
    }
    $excluded=$manifest.SelectNodes("//*[local-name()='ExcludedDirectory']")
    if($excluded.Count -ne 1 -or $excluded[0].InnerText -ne '$(KnownFolder:LocalAppData)\HybridMount.NET\Mounting\Thumbnails'){throw '共享缩略图目录的精确虚拟化排除不符。'}
    if(-not $manifest.SelectNodes("//*[local-name()='Capability' and @Name='unvirtualizedResources']").Count){throw '缺少共享缓存所需的受限能力。'}
    Write-Host "包验证通过：$Platform / $version / 自包含 / Shell配置=$ShellProfile / 原生状态及维护入口保留，桌面效果另行验收。"

} finally { $archive.Dispose() }
