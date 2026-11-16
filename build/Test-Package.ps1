# 检查实际交付包，不把成功编译当作打包内容正确的证据。
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][ValidateSet('x64','ARM64')][string]$Platform,[switch]$ExpectCloudThumbnails,[ValidateSet('Sideload','Store')][string]$DistributionChannel='Sideload',[ValidateSet('Full','Small')][string]$RuntimeMode='Full',[ValidateSet('Native','Menus','MenusAndThumbnails')][string]$ShellProfile='MenusAndThumbnails')
$ErrorActionPreference = 'Stop'
# bundle清单采用小写架构，资源门禁保留工程的规范拼写。
$Platform=if($Platform -ieq 'ARM64'){'ARM64'}else{'x64'}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($Path))
try {
    $entries = @{}
    foreach ($entry in $archive.Entries) { $entries[$entry.FullName.Replace('\','/')] = $entry }
    foreach ($required in @('Privacy/ChineseSimplified.md','Privacy/ChineseTraditional.md','Privacy/English.md','AppxManifest.xml','resources.pri','HybridMount.Ext.resources.dll','HybridMount.exe','HybridMount.dll','HybridMount.runtimeconfig.json','HybridMount.Ext.dll','HybridMount.Stock.exe','e_sqlite3.dll','ThirdParty/Dependencies.txt','ThirdParty/Resolved/Microsoft.NETCore.App.Runtime.win-x64/10.0.12/LICENSE.TXT','ThirdParty/Resolved/Microsoft.NETCore.App.Runtime.win-x64/10.0.12/THIRD-PARTY-NOTICES.TXT','ThirdParty/nlohmann/LICENSE.txt','AWSSDK.Core.dll','AWSSDK.S3.dll','WebDAVClient.dll','AWSSDK.SQS.dll','ThirdParty/WebDAVClient/LICENSE.txt','ThirdParty/WebDAVClient/NOTICE.txt','ThirdParty/AWSSDK/License.txt','ThirdParty/AWSSDK/Notice.txt','ThirdParty/AWSSDK/SqsPackage.txt','Assets/HybridMount.ico','Assets/HybridMount.png','Assets/SyncIssue.png','Assets/SyncExcluded.png','Assets/SyncExcluded.ico','Assets/SyncIssue.ico')) {
        if (-not $entries.ContainsKey($required)) { throw "包缺少 $required" }
    }
    if($RuntimeMode -eq 'Full') {
        foreach($required in @('coreclr.dll','System.Private.CoreLib.dll','Microsoft.ui.xaml.dll','Microsoft.WindowsAppRuntime.dll')){if(-not $entries.ContainsKey($required)){throw "Full包缺少框架载荷：$required"}}
    } else {
        foreach($forbidden in @('coreclr.dll','System.Private.CoreLib.dll','Microsoft.ui.xaml.dll','Microsoft.WindowsAppRuntime.dll')){if($entries.ContainsKey($forbidden)){throw "Small包夹带完整框架：$forbidden"}}
    }
    # 改名后的包只允许当前主映像，防止增量构建夹带旧入口。
    foreach($obsolete in @('HybridMount.NET.exe','HybridMount.NET.dll','HybridMount.NET.deps.json','HybridMount.NET.runtimeconfig.json')){
        if($entries.ContainsKey($obsolete)){throw "包夹带旧主程序入口：$obsolete"}
    }
    function Read-Bytes([string]$entryName) {
        $stream = $entries[$entryName].Open(); $buffer = [IO.MemoryStream]::new()
        try { $stream.CopyTo($buffer); return ,$buffer.ToArray() } finally { $stream.Dispose(); $buffer.Dispose() }
    }
    [xml]$manifest = [Text.Encoding]::UTF8.GetString((Read-Bytes 'AppxManifest.xml')).TrimStart([char]0xfeff)
    [xml]$props = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\Directory.Build.props') -Raw
    $version = [string]$props.Project.PropertyGroup[0].Version
    $binaryVersion=if(([version]$version).Revision -lt 0) { "$version.0" } else { $version }
    if ($manifest.Package.Identity.Version -ne $binaryVersion -or $manifest.Package.Identity.Name -cne [string]$props.Project.PropertyGroup[0].HybridMountPackageName -or $manifest.Package.Identity.Publisher -cne [string]$props.Project.PropertyGroup[0].HybridMountPublisher -or $manifest.Package.Properties.PublisherDisplayName -cne [string]$props.Project.PropertyGroup[0].HybridMountPublisherDisplayName) { throw 'MSIX 产品身份或版本不一致。' }
    if ($manifest.Package.Identity.ProcessorArchitecture -ne $Platform.ToLowerInvariant()) { throw '包声明架构不正确。' }
    # WinUI的编译XAML嵌入PRI；按真实资源URI验证迁移后的窗口、页面、控件和共享样式。
    $resourceWork=Join-Path $PSScriptRoot ("..\artifacts\validation\package-resources-"+$Platform+"-"+$binaryVersion+'-'+$RuntimeMode)
    [IO.Directory]::CreateDirectory($resourceWork)|Out-Null
    $priFile=Join-Path $resourceWork 'resources.pri';$resourceXml=Join-Path $resourceWork 'resources.xml'
    [IO.File]::WriteAllBytes($priFile,(Read-Bytes 'resources.pri'))
    $makepri=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin\10.0.26100.0\x64\makepri.exe'
    & $makepri dump /if $priFile /of $resourceXml /o *> (Join-Path $resourceWork 'dump.log')
    if($LASTEXITCODE -ne 0){throw '无法核验包内XAML资源索引。'}
    [xml]$resourceIndex=[IO.File]::ReadAllText($resourceXml)
    # 核对实际PRI中的三语言安装显示名，避免仅修改源清单而仍打入旧实验名。
    $displayNames=$resourceIndex.SelectNodes("//*[local-name()='NamedResource' and @name='PackageDisplayName']/*[local-name()='Candidate' and @type='String']")
    if($displayNames.Count -ne 3 -or @($displayNames | Where-Object {$_.SelectSingleNode("./*[local-name()='Value']").InnerText -ne 'HybridMount.NET'}).Count){throw '安装包显示名称未统一为HybridMount.NET。'}
    $resourceRoot='ms-resource://'+$manifest.Package.Identity.Name+'/Files/'
    foreach($name in @('App.xbf','Views/Windows/MainWindow.xbf','Views/Pages/HomePage.xbf','Views/Pages/MountsPage.xbf','Views/Pages/TasksPage.xbf','Views/Pages/LogsPage.xbf','Views/Pages/SettingsPage.xbf','Controls/MountCard.xbf','Controls/SettingRow.xbf','Views/Dialogs/MountBackupDialog.xbf','Views/Dialogs/ConflictResolutionContentDialog.xbf','Views/Dialogs/Mounting/WebDAVConfigurationContentDialog.xbf','Views/Dialogs/Mounting/S3ConfigurationContentDialog.xbf','Views/Dialogs/Mounting/SynologyConfigurationContentDialog.xbf','Resources/SharedStyles.xbf')){
        $resource=$resourceIndex.SelectNodes("//*[local-name()='NamedResource' and @uri='"+$resourceRoot+$name+"']/*[local-name()='Candidate' and @type='EmbeddedData']")
        if($resource.Count -ne 1){throw "包内编译资源缺失或重复：$name"}
    }
    foreach($language in @('zh-CN','en-US','zh-HK')){
        $name="$language/HybridMount.Ext.resources.dll.mui";if(-not $entries.ContainsKey($name)){throw "缺少原生语言资源：$language"}
        [IO.Directory]::CreateDirectory((Join-Path $resourceWork $language))|Out-Null
        [IO.File]::WriteAllBytes((Join-Path $resourceWork $name),(Read-Bytes $name))
    }
    [IO.File]::WriteAllBytes((Join-Path $resourceWork 'HybridMount.Ext.resources.dll'),(Read-Bytes 'HybridMount.Ext.resources.dll'))
    . (Join-Path $PSScriptRoot 'Read-ManagedResources.ps1')
    $managedDump=[Xml.XmlDocument]::new();$managedRoot=$managedDump.CreateElement('managed');[void]$managedDump.AppendChild($managedRoot)
    foreach($language in @('zh-CN','en-US','zh-HK')) {
        $imageName=if($language -eq 'zh-CN'){'HybridMount.dll'}else{"$language/HybridMount.resources.dll"}
        if(-not $entries.ContainsKey($imageName)){throw "缺少标准C#语言卫星：$imageName"}
        $imagePath=Join-Path $resourceWork $imageName
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($imagePath))|Out-Null
        [IO.File]::WriteAllBytes($imagePath,(Read-Bytes $imageName))
        $culture=if($language -eq 'zh-CN'){''}else{$language}
        $assemblyName=if($language -eq 'zh-CN'){'HybridMount'}else{'HybridMount.resources'}
        $resourceName=if($language -eq 'zh-CN'){'HybridMount.Resources.Messages.resources'}else{"HybridMount.Resources.Messages.$language.resources"}
        $values=Read-ManagedStrings $imagePath $resourceName $culture $assemblyName
        $element=$managedDump.CreateElement('language');$element.SetAttribute('name',$language);[void]$managedRoot.AppendChild($element)
        foreach($name in ($values.Keys | Sort-Object)) {
            $item=$managedDump.CreateElement('data');$item.SetAttribute('name',$name);[void]$element.AppendChild($item)
            $value=$managedDump.CreateElement('value');$value.InnerText=$values[$name];[void]$item.AppendChild($value)
        }
    }
    $managedXml=Join-Path $resourceWork 'managed-resources.xml';$managedDump.Save($managedXml)
    # 复用MSBuild中的资源任务与已有PRI转储，不再调用独立语言校验脚本。
    $resourceMsbuild=& (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe') -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\amd64\MSBuild.exe' | Select-Object -First 1
    if(-not $resourceMsbuild){throw '需要Visual Studio MSBuild进行资源契约检查。'}
    & $resourceMsbuild (Join-Path $PSScriptRoot 'HybridMount.ResourceValidation.proj') /nologo /v:minimal "/p:LocalizationPriDump=$resourceXml" "/p:LocalizationMuiRoot=$resourceWork" "/p:LocalizationManagedDump=$managedXml" "/p:Platform=$Platform"
    if($LASTEXITCODE -ne 0){throw '包内三语言资源校验失败。'}
    # 主入口显式保留打包桌面运行环境；隐藏维护入口使用真实注册视图的win32App／mediumIL组合。
    $apps=$manifest.SelectNodes("//*[local-name()='Applications']/*[local-name()='Application']")
    $main=@($apps|Where-Object {$_.Id -eq 'App'})
    $maintenance=@($apps|Where-Object {$_.Id -eq 'ShellMaintenance'})
    if($apps.Count -ne 2 -or $main.Count -ne 1 -or $maintenance.Count -ne 1){throw '应用及隐藏维护入口缺失或重复。'}
    $runtimeNamespace='http://schemas.microsoft.com/appx/manifest/uap/windows10/10'
    $entry=$maintenance[0]
    if($main[0].Executable -ne 'HybridMount.exe' -or $main[0].EntryPoint -ne 'Windows.FullTrustApplication' -or
       $main[0].GetAttribute('RuntimeBehavior',$runtimeNamespace) -ne 'packagedClassicApp' -or $main[0].GetAttribute('TrustLevel',$runtimeNamespace) -ne 'mediumIL' -or $entry.HasAttribute('EntryPoint') -or
       $entry.Executable -ne 'HybridMount.exe' -or $entry.GetAttribute('RuntimeBehavior',$runtimeNamespace) -ne 'win32App' -or
       $entry.GetAttribute('TrustLevel',$runtimeNamespace) -ne 'mediumIL' -or $entry.VisualElements.AppListEntry -ne 'none'){
        throw '隐藏维护入口运行行为不正确，不能据此进行真实Shell清理。'
    }
    $startup=@($main[0].Extensions.Extension | Where-Object {$_.Category -eq 'windows.startupTask'})
    if($startup.Count -ne 1 -or $startup[0].Executable -ne 'HybridMount.exe'){throw '登录启动入口未使用当前主映像。'}
    $expectedMachine = if ($Platform -eq 'ARM64') { 0xaa64 } else { 0x8664 }
    foreach ($entryName in (@('HybridMount.exe','HybridMount.dll','HybridMount.Ext.dll','HybridMount.Stock.exe','e_sqlite3.dll','Microsoft.WindowsAppRuntime.Bootstrap.dll')+$(if($RuntimeMode -eq 'Full'){@('coreclr.dll','hostfxr.dll','hostpolicy.dll','Microsoft.ui.xaml.dll','Microsoft.WindowsAppRuntime.dll')}))) {
        $bytes = Read-Bytes $entryName
        $pe = [BitConverter]::ToInt32($bytes,0x3c)
        if ([BitConverter]::ToUInt16($bytes,$pe+4) -ne $expectedMachine) { throw "$entryName 架构不正确。" }
    }
    Assert-ManagedImageArchitecture (Read-Bytes 'Microsoft.WinUI.dll') $Platform
    # C++后台和扩展不得含CLR头，也不能意外携带隔离测试操作。
    foreach($entryName in @('HybridMount.Stock.exe','HybridMount.Ext.dll')){
        $bytes=Read-Bytes $entryName;$pe=[BitConverter]::ToInt32($bytes,0x3c);$optional=$pe+24
        if([BitConverter]::ToUInt16($bytes,$optional) -ne 0x20b -or [BitConverter]::ToUInt32($bytes,$optional+112+14*8) -ne 0){throw "原生宿主含CLR入口：$entryName"}
        if([Text.Encoding]::ASCII.GetString($bytes).Contains('testSetPin')){throw "发布包混入测试专用原生操作：$entryName"}
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
    $runtime = [Text.Encoding]::UTF8.GetString((Read-Bytes 'HybridMount.runtimeconfig.json')) | ConvertFrom-Json
    if($RuntimeMode -eq 'Full') {
        if($runtime.runtimeOptions.framework -or $runtime.runtimeOptions.frameworks){throw 'Full仍依赖共享.NET运行时。'}
    } else {
        $frameworks=@($runtime.runtimeOptions.framework)+@($runtime.runtimeOptions.frameworks)
        if(-not @($frameworks|Where-Object{$_.name -eq 'Microsoft.NETCore.App' -and $_.version -like '10.*'}).Count){throw 'Small没有正确声明.NET 10共享框架。'}
        if(-not $manifest.SelectNodes("//*[local-name()='PackageDependency' and starts-with(@Name,'Microsoft.WindowsAppRuntime.')]" ).Count){throw 'Small MSIX缺少Windows App Runtime框架依赖。'}
    }
    if ($entries.Keys | Where-Object { $_ -match '(?i)EPaperKit|ProKit|Square44x44Logo|StoreLogo' }) { throw '包内出现旧产品资源或组件。' }
    $text = $manifest.OuterXml
    foreach ($registration in @('windows.startupTask','HybridMountStartup','ShellMaintenance')) {
        if (-not $text.Contains($registration)) { throw "清单缺少 $registration" }
    }
    # 校验实际选择的唯一注册组合，不允许额外文件类型接管或未实现处理器CLSID。
    if($ExpectCloudThumbnails -and $ShellProfile -ne 'MenusAndThumbnails'){throw '缩略图预期与Shell配置不一致。'}
    if($manifest.SelectNodes("//*[local-name()='Extension' and @Category='windows.fileTypeAssociation']").Count){throw '不得接管全局文件类型。'}
    $cloud=$manifest.SelectNodes("//*[local-name()='CloudFiles']");if($cloud.Count -ne 1 -or $cloud[0].IconResource -ne 'Assets\HybridMount.png'){throw '云提供程序声明不正确。'}
    $classes=@($manifest.SelectNodes("//*[local-name()='ComServer']//*[local-name()='Class']"));$verbs=@($manifest.SelectNodes("//*[local-name()='Verb']"));$menu='59964FC1-D076-4DC1-9778-01F5833A5D4A';$thumb='A749B295-9CB5-4F08-98D4-00EB47345E09'
    $expected=if($ShellProfile -eq 'Native'){@()}elseif($ShellProfile -eq 'Menus'){@($menu)}else{@($menu,$thumb)}
    if($classes.Count -ne $expected.Count -or @($classes|Where-Object {$_.Id -notin $expected}).Count){throw 'COM类与所选配置不一致。'}
    foreach($class in $classes){if($class.ParentNode.LocalName -ne 'ExeServer' -or $class.ParentNode.Executable -ne 'HybridMount.Stock.exe' -or $class.ParentNode.Arguments -ne '--shell-server'){throw 'Shell类没有绑定独立早期宿主。'}}
    if($ShellProfile -eq 'Native'){if($cloud[0].ChildNodes.Count -or $verbs.Count){throw '原生配置意外含扩展。'}}
    elseif($ShellProfile -eq 'Menus'){if($cloud[0].ChildNodes.Count -or $verbs.Count -ne 3 -or @($verbs|Where-Object {$_.Clsid -ne $menu}).Count){throw '独立动态菜单注册不符。'}}
    else{
        if($cloud[0].ChildNodes.Count -ne 5 -or $verbs.Count -ne 3 -or @($verbs|Where-Object {$_.Clsid -ne $menu}).Count -or $cloud[0].SelectSingleNode("*[local-name()='CloudFilesContextMenus']").ChildNodes.Count){throw '云菜单和缩略图组合不完整或重复。'}
        $handler=$cloud[0].SelectSingleNode("*[local-name()='ThumbnailProviderHandler']");if(!$handler -or $handler.Clsid -ne $thumb){throw '云缩略图入口不符。'}
        foreach($name in @('CustomStateHandler','ExtendedPropertyHandler','BannersHandler')){$node=$cloud[0].SelectSingleNode("*[local-name()='$name']");if(!$node -or $node.Clsid -ne '00000000-0000-0000-0000-000000000000'){throw '未提供处理器必须明确为GUID_NULL，不得登记虚构COM类。'}}
    }
    if($manifest.SelectNodes("//*[local-name()='ExcludedDirectory' or local-name()='FileSystemWriteVirtualization']").Count -or $manifest.SelectNodes("//*[local-name()='Capability' and @Name='unvirtualizedResources']").Count){throw '当前包缓存使用实际LocalState，不得申请unvirtualizedResources或排除虚拟化。'}
    if($DistributionChannel -eq 'Store' -and ($RuntimeMode -ne 'Full' -or $entries.ContainsKey('AppxSignature.p7x'))){throw 'Store候选必须为Full且不可使用侧载签名。'}
    Write-Host "包验证通过：$Platform / $version / $RuntimeMode / Shell配置=$ShellProfile / 原生状态及维护入口保留，桌面效果另行验收。"

} finally { $archive.Dispose() }
