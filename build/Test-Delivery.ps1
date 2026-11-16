# 检查实际目录和ZIP的载荷，不启动产品界面或连接挂载。
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Path,[Parameter(Mandatory)][ValidateSet('x64','ARM64')][string]$Platform,[ValidateSet('Full','Small')][string]$RuntimeMode='Full')
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Read-ManagedResources.ps1')
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$props=Get-Content (Join-Path $repo 'Directory.Build.props') -Raw
$version=[string]$props.Project.PropertyGroup[0].Version
$source=[IO.Path]::GetFullPath($Path);$kind=if(Test-Path -LiteralPath $source -PathType Container){'directory'}else{'zip'}
$work=Join-Path $repo "artifacts/validation/$($version.Replace('.',''))-work/delivery-$Platform-$RuntimeMode-$kind"
[IO.Directory]::CreateDirectory($work)|Out-Null
if($kind -eq 'zip') {
    $root=Join-Path $work ('extracted-'+[Guid]::NewGuid().ToString('N'))
    [IO.Compression.ZipFile]::ExtractToDirectory($source,$root)
} else {$root=$source}
$required=@('HybridMount.exe','HybridMount.dll','HybridMount.deps.json','HybridMount.runtimeconfig.json','HybridMount.Stock.exe','HybridMount.Ext.dll','HybridMount.Ext.resources.dll','HybridMount.pri','e_sqlite3.dll','AWSSDK.Core.dll','AWSSDK.S3.dll','AWSSDK.SQS.dll','WebDAVClient.dll','Assets/HybridMount.ico','Assets/HybridMount.png','Assets/SyncIssue.ico','Assets/SyncExcluded.ico','Assets/SyncIssue.png','Assets/SyncExcluded.png','ThirdParty/nlohmann/LICENSE.txt','ThirdParty/WebDAVClient/LICENSE.txt','ThirdParty/WebDAVClient/NOTICE.txt','ThirdParty/AWSSDK/License.txt','ThirdParty/AWSSDK/Notice.txt','ThirdParty/AWSSDK/SqsPackage.txt')
foreach($language in @('zh-CN','en-US','zh-HK')){$required+="$language/HybridMount.Ext.resources.dll.mui"}
$required+=@('en-US/HybridMount.resources.dll','zh-HK/HybridMount.resources.dll')
foreach($file in $required){if(-not(Test-Path -LiteralPath (Join-Path $root $file) -PathType Leaf)){throw "交付物缺少：$file"}}
foreach($obsolete in @('HybridMount.NET.exe','HybridMount.NET.dll')){if(Test-Path -LiteralPath (Join-Path $root $obsolete)){throw '交付物夹带旧主程序。'}}
$runtime=Get-Content (Join-Path $root 'HybridMount.runtimeconfig.json') -Raw|ConvertFrom-Json
$frameworks=@($runtime.runtimeOptions.framework)+@($runtime.runtimeOptions.frameworks)
$payload=@('coreclr.dll','System.Private.CoreLib.dll','Microsoft.ui.xaml.dll','Microsoft.WindowsAppRuntime.dll')
if($RuntimeMode -eq 'Full') {
    if(@($frameworks|Where-Object{$_}).Count){throw 'Full不应依赖共享.NET框架。'}
    foreach($file in $payload){if(-not(Test-Path -LiteralPath (Join-Path $root $file))){throw "Full缺少框架载荷：$file"}}
} else {
    if(-not @($frameworks|Where-Object{$_.name -eq 'Microsoft.NETCore.App' -and $_.version -like '10.*'}).Count){throw 'Small没有声明.NET 10共享框架。'}
    foreach($file in $payload){if(Test-Path -LiteralPath (Join-Path $root $file)){throw "Small夹带完整框架：$file"}}
    if(-not(Test-Path -LiteralPath (Join-Path $root 'Microsoft.WindowsAppRuntime.Bootstrap.dll'))){throw 'Small目录缺少Windows App Runtime引导组件。'}
}
$machine=if($Platform -eq 'ARM64'){0xaa64}else{0x8664}
foreach($file in (@('HybridMount.exe','HybridMount.dll','HybridMount.Stock.exe','HybridMount.Ext.dll','e_sqlite3.dll','Microsoft.WindowsAppRuntime.Bootstrap.dll')+$(if($RuntimeMode -eq 'Full'){@('coreclr.dll','hostfxr.dll','hostpolicy.dll','Microsoft.ui.xaml.dll','Microsoft.WindowsAppRuntime.dll')}))) {
    $bytes=[IO.File]::ReadAllBytes((Join-Path $root $file));$pe=[BitConverter]::ToInt32($bytes,0x3c)
    if([BitConverter]::ToUInt16($bytes,$pe+4) -ne $machine){throw "交付物架构不正确：$file"}
}
Assert-ManagedImageArchitecture ([IO.File]::ReadAllBytes((Join-Path $root 'Microsoft.WinUI.dll'))) $Platform
foreach($file in @('HybridMount.exe','HybridMount.dll','HybridMount.Stock.exe','HybridMount.Ext.dll')) {
    if([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $root $file)).FileVersion -ne $version){throw "交付物版本不正确：$file"}
}
foreach($file in @('HybridMount.Stock.exe','HybridMount.Ext.dll')) {
    $bytes=[IO.File]::ReadAllBytes((Join-Path $root $file));$pe=[BitConverter]::ToInt32($bytes,0x3c)
    if([BitConverter]::ToUInt32($bytes,$pe+24+112+14*8) -ne 0 -or [Text.Encoding]::ASCII.GetString($bytes).Contains('testSetPin')){throw '交付物含托管原生宿主或测试专用操作。'}
}
# 直接读取实际PE中的三语言RESX，PRI和MUI继续交给同一资源门禁。
$dump=[Xml.XmlDocument]::new();$managed=$dump.CreateElement('managed');[void]$dump.AppendChild($managed)
foreach($language in @('zh-CN','en-US','zh-HK')) {
    $culture=if($language -eq 'zh-CN'){''}else{$language};$name=if($culture){'HybridMount.resources'}else{'HybridMount'}
    $file=if($culture){"$language/HybridMount.resources.dll"}else{'HybridMount.dll'}
    $resource=if($culture){"HybridMount.Resources.Messages.$language.resources"}else{'HybridMount.Resources.Messages.resources'}
    $values=Read-ManagedStrings (Join-Path $root $file) $resource $culture $name
    $element=$dump.CreateElement('language');$element.SetAttribute('name',$language);[void]$managed.AppendChild($element)
    foreach($key in ($values.Keys|Sort-Object)){$item=$dump.CreateElement('data');$item.SetAttribute('name',$key);$value=$dump.CreateElement('value');$value.InnerText=$values[$key];[void]$item.AppendChild($value);[void]$element.AppendChild($item)}
}
$managedXml=Join-Path $work 'managed.xml';$dump.Save($managedXml)
$sdk=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin/10.0.26100.0/x64'
$pri=Join-Path $work 'resources.xml'
& (Join-Path $sdk 'makepri.exe') dump /if (Join-Path $root 'HybridMount.pri') /of $pri /o *> (Join-Path $work 'makepri.log')
if($LASTEXITCODE -ne 0){throw '交付物PRI转储失败。'}
$msbuild=& (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe') -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\amd64\MSBuild.exe'|Select-Object -First 1
& $msbuild (Join-Path $PSScriptRoot 'HybridMount.ResourceValidation.proj') /nologo /v:minimal "/p:LocalizationPriDump=$pri" "/p:LocalizationMuiRoot=$root" "/p:LocalizationManagedDump=$managedXml" "/p:Platform=$Platform"
if($LASTEXITCODE -ne 0){throw '交付物实际资源校验失败。'}
[xml]$index=Get-Content $pri -Raw
foreach($file in @('App.xbf','Views/Windows/MainWindow.xbf','Views/Pages/HomePage.xbf','Views/Pages/TasksPage.xbf','Resources/SharedStyles.xbf')) {
    if(-not $index.SelectNodes("//*[local-name()='NamedResource' and substring(@uri,string-length(@uri)-string-length('$file')+1)='$file']").Count){throw "交付物缺少编译XAML：$file"}
}
$files=@(Get-ChildItem -LiteralPath $root -Recurse -File|ForEach-Object{[pscustomobject]@{Path=[IO.Path]::GetRelativePath($root,$_.FullName);Bytes=$_.Length;SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
[ordered]@{Version=$version;Platform=$Platform;RuntimeMode=$RuntimeMode;Kind=$kind;Source=$source;FileCount=$files.Count;Bytes=($files|Measure-Object Bytes -Sum).Sum;Files=$files;Status='Passed';Launch='NotExecuted'}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $work 'delivery.json') -Encoding utf8
Write-Host "交付检查通过：$Platform / $RuntimeMode / $kind / $($files.Count)文件。"
