# 仅处理构建副本；可关闭唯一缩略图注册，保留应用、启动和维护入口及其他虚拟化行为。
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Source,[Parameter(Mandatory)][string]$Destination,[ValidateSet('true','false')][string]$Thumbnails='true')
$ErrorActionPreference='Stop'
[xml]$manifest=[IO.File]::ReadAllText($Source)
if($Thumbnails -eq 'false'){
    # SDK26100要求CloudFiles子组完整出现或整体为空；禁用时保留IconResource。
    foreach($cloud in @($manifest.SelectNodes("//*[local-name()='CloudFiles']"))){while($cloud.ChildNodes.Count){[void]$cloud.RemoveChild($cloud.FirstChild)}}
    foreach($node in @($manifest.SelectNodes("//*[local-name()='Extension' and @Category='windows.comServer']"))){
        $classes=@($node.SelectNodes(".//*[local-name()='Class']"))
        if($classes.Count -ne 1 -or $classes[0].Id -ne 'A65802EB-4D1C-4C7E-A519-34C76B9BB281'){throw '构建副本包含未知COM注册，拒绝移除。'}
        [void]$node.ParentNode.RemoveChild($node)
    }
}else{throw '0.0.9.37缩略图候选注册尚未通过原生菜单兼容门槛。'}
$text=$manifest.OuterXml
$absolute=[IO.Path]::GetFullPath($Destination)
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($absolute))
if(-not [IO.File]::Exists($absolute) -or [IO.File]::ReadAllText($absolute) -ne $text){[IO.File]::WriteAllText($absolute,$text,[Text.UTF8Encoding]::new($false))}
