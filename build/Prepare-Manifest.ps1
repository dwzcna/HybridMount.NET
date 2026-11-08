# 只修改构建副本；三个配置分别验证，保持主应用、启动任务及维护入口。
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Source,[Parameter(Mandatory)][string]$Destination,[ValidateSet('Native','Menus','MenusAndThumbnails')][string]$ShellProfile='Menus',[ValidateSet('true','false')][string]$Thumbnails)
$ErrorActionPreference='Stop'
if($Thumbnails){if($PSBoundParameters.ContainsKey('ShellProfile')){throw '不要混用旧缩略图参数与ShellProfile。'};$ShellProfile=if($Thumbnails -eq 'true'){'MenusAndThumbnails'}else{'Native'}}
[xml]$manifest=[IO.File]::ReadAllText($Source)
$foundation='http://schemas.microsoft.com/appx/manifest/foundation/windows10'
$d3='http://schemas.microsoft.com/appx/manifest/desktop/windows10/3';$d4='http://schemas.microsoft.com/appx/manifest/desktop/windows10/4';$d5='http://schemas.microsoft.com/appx/manifest/desktop/windows10/5';$com='http://schemas.microsoft.com/appx/manifest/com/windows10'
$extensions=$manifest.SelectSingleNode("//*[local-name()='Application' and @Id='App']/*[local-name()='Extensions']")
if(!$extensions){throw '主应用扩展节点缺失。'}
foreach($node in @($extensions.ChildNodes)){if($node.LocalName -eq 'Extension' -and $node.Category -in @('windows.cloudFiles','windows.comServer','windows.fileExplorerContextMenus')){[void]$extensions.RemoveChild($node)}}
function Add-Element($parent,[string]$prefix,[string]$name,[string]$space,[hashtable]$attributes=@{}){
    $element=$manifest.CreateElement($prefix,$name,$space);foreach($key in $attributes.Keys){$element.SetAttribute($key,[string]$attributes[$key])};[void]$parent.AppendChild($element);return $element
}
$extension=Add-Element $extensions 'desktop3' 'Extension' $d3 @{Category='windows.cloudFiles'}
$cloud=Add-Element $extension 'desktop3' 'CloudFiles' $d3 @{IconResource='Assets\HybridMount.png'}
$menuId='F4A8FD0B-4055-4F23-8CC0-91B736B3E491';$thumbId='A65802EB-4D1C-4C7E-A519-34C76B9BB281'
if($ShellProfile -eq 'MenusAndThumbnails'){
    # SDK要求完整子组；没有实现的处理器不设置CLSID，原生状态仍由系统提供。
    [void](Add-Element $cloud 'desktop3' 'CustomStateHandler' $d3)
    [void](Add-Element $cloud 'desktop3' 'ThumbnailProviderHandler' $d3 @{Clsid=$thumbId})
    [void](Add-Element $cloud 'desktop3' 'ExtendedPropertyHandler' $d3)
    [void](Add-Element $cloud 'desktop3' 'BannersHandler' $d3)
    $menus=Add-Element $cloud 'desktop3' 'CloudFilesContextMenus' $d3
    [void](Add-Element $menus 'desktop3' 'Verb' $d3 @{Id='HybridMount';Clsid=$menuId})
}elseif($ShellProfile -eq 'Menus'){
    $menuExtension=Add-Element $extensions 'desktop4' 'Extension' $d4 @{Category='windows.fileExplorerContextMenus'}
    $menus=Add-Element $menuExtension 'desktop4' 'FileExplorerContextMenus' $d4
    foreach($type in @('*','Directory','Directory\Background')){$item=Add-Element $menus 'desktop5' 'ItemType' $d5 @{Type=$type};[void](Add-Element $item 'desktop5' 'Verb' $d5 @{Id='HybridMount';Clsid=$menuId})}
}
if($ShellProfile -ne 'Native'){
    $serverExtension=Add-Element $extensions 'com' 'Extension' $com @{Category='windows.comServer'}
    $servers=Add-Element $serverExtension 'com' 'ComServer' $com
    $server=Add-Element $servers 'com' 'ExeServer' $com @{Executable='HybridMount.NET.exe';Arguments='--shell-server';DisplayName='HybridMount Shell'}
    [void](Add-Element $server 'com' 'Class' $com @{Id=$menuId;DisplayName='HybridMount 动态菜单'})
    if($ShellProfile -eq 'MenusAndThumbnails'){[void](Add-Element $server 'com' 'Class' $com @{Id=$thumbId;DisplayName='HybridMount 数据库缩略图'})}
}
$absolute=[IO.Path]::GetFullPath($Destination);[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($absolute))
$text=$manifest.OuterXml;if(-not [IO.File]::Exists($absolute) -or [IO.File]::ReadAllText($absolute) -ne $text){[IO.File]::WriteAllText($absolute,$text,[Text.UTF8Encoding]::new($false))}
