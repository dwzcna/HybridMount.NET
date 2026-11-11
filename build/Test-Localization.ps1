# 三语言资源契约、真实PRI和MUI共同核验；缺键或参数漂移阻止构建／交付。
[CmdletBinding()]
param([string]$PriPath,[string]$MuiRoot,[ValidateSet('x64','ARM64')][string]$Platform='x64')
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskCatalog=Get-Content -LiteralPath (Join-Path $taskRoot 'HybridMount\Resources\UiCatalog.json') -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable
$taskLanguages=@('zh-CN','en-US','zh-HK');$taskTexts=@{}
function Parameters([string]$text){return (([regex]::Matches($text.Replace('{{','').Replace('}}',''),'\{(\d+)(?:,[^}:]+)?(?::[^}]+)?\}')|ForEach-Object {$_.Groups[1].Value})|Sort-Object)-join ','}
foreach($taskLanguage in $taskLanguages){
 [xml]$taskXml=Get-Content -LiteralPath (Join-Path $taskRoot "HybridMount\Strings\$taskLanguage\Resources.resw") -Raw -Encoding utf8
 $taskValues=@{};foreach($taskEntry in $taskXml.root.data){if($taskValues.ContainsKey([string]$taskEntry.name)){throw "duplicate resource: $taskLanguage/$($taskEntry.name)"};$taskValues[[string]$taskEntry.name]=[string]$taskEntry.value}
 if($taskValues.Count -ne $taskCatalog.Count){throw "resource key count: $taskLanguage"}
 foreach($taskKey in $taskCatalog.Keys){if(-not $taskValues.ContainsKey($taskKey)-or (Parameters $taskValues[$taskKey]) -ne (Parameters $taskCatalog[$taskKey].Source)){throw "resource key/parameters: $taskLanguage/$taskKey"}}
 $taskTexts[$taskLanguage]=$taskValues
}
# 只比较明确的产品资源调用，动态绑定由资源消息契约覆盖。
foreach($taskFile in Get-ChildItem (Join-Path $taskRoot 'HybridMount') -Include *.cs,*.xaml -Recurse -File|Where-Object {$_.FullName -notmatch '\\(obj|bin)\\'}){
 foreach($taskMatch in [regex]::Matches([IO.File]::ReadAllText($taskFile.FullName),'Text_[A-F0-9]{12}')){if(-not $taskCatalog.ContainsKey($taskMatch.Value)){throw "unknown UI resource: $($taskMatch.Value)"}}
}
$taskNative=@('HybridMount.NET','释放空间','始终保留在此设备上','查看同步问题','在应用中定位此文件或目录的同步问题；不会自动上传。','管理此挂载的离线缓存；本地修改会受到保护。','同步问题','上传排除')
foreach($taskLanguage in $taskLanguages){
 $taskRc=Get-Content -LiteralPath (Join-Path $taskRoot "HybridMount.Ext\Strings\$taskLanguage\NativeStrings.rc") -Raw -Encoding utf8
 for($taskIndex=0;$taskIndex -lt $taskNative.Count;$taskIndex++){
  $taskKey='Text_'+[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($taskNative[$taskIndex]))).Substring(0,12)
  $taskExpected=if($taskIndex -eq 0){$taskNative[0]}else{$taskTexts[$taskLanguage][$taskKey]}
  if(-not $taskRc.Contains(('    '+(1001+$taskIndex)+' "'+$taskExpected+'"'))){throw "MUI source drift: $taskLanguage/$taskIndex"}
 }
}
if($PriPath){
 $taskPri=[IO.Path]::GetFullPath($PriPath);$taskDump=Join-Path $taskRoot ('artifacts\validation\pri-language-'+[guid]::NewGuid().ToString('N')+'.xml')
 & (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin\10.0.26100.0\x64\makepri.exe') dump /if $taskPri /of $taskDump /o | Out-Null
 if($LASTEXITCODE -ne 0){throw 'PRI language dump failed'}
 [xml]$taskIndexXml=[IO.File]::ReadAllText($taskDump)
 foreach($taskKey in $taskCatalog.Keys){$taskResource=$taskIndexXml.SelectNodes("//*[local-name()='NamedResource' and @name='$taskKey']/*[local-name()='Candidate']")
  foreach($taskLanguage in $taskLanguages){$taskCandidates=@($taskResource|Where-Object {$_.qualifiers -match ('(?i)Language-'+[regex]::Escape($taskLanguage)+'(?:;|$)')});if($taskCandidates.Count -ne 1 -or $taskCandidates[0].Value -cne $taskTexts[$taskLanguage][$taskKey]){throw "PRI language missing/drift: $taskLanguage/$taskKey"}}
 }
}
if($MuiRoot){
 if(-not ('HybridMount.MuiCheck' -as [type])){Add-Type -TypeDefinition 'using System;using System.Runtime.InteropServices;using System.Text;namespace HybridMount{public static class MuiCheck{[DllImport("kernel32",CharSet=CharSet.Unicode,SetLastError=true)]public static extern IntPtr LoadLibraryEx(string name,IntPtr file,uint flags);[DllImport("user32",CharSet=CharSet.Unicode)]public static extern int LoadString(IntPtr module,uint id,StringBuilder buffer,int count);[DllImport("kernel32")]public static extern bool FreeLibrary(IntPtr module);}}'}
 foreach($taskLanguage in $taskLanguages){
  $taskMui=Join-Path $MuiRoot "$taskLanguage\HybridMount.Ext.dll.mui";$taskBytes=[IO.File]::ReadAllBytes($taskMui);$taskPe=[BitConverter]::ToInt32($taskBytes,0x3c)
  if([BitConverter]::ToUInt16($taskBytes,$taskPe+4) -ne $(if($Platform -eq 'ARM64'){0xaa64}else{0x8664})){throw "MUI architecture: $taskLanguage"}
  if([BitConverter]::ToUInt32($taskBytes,$taskPe+24+112+14*8) -ne 0){throw 'MUI contains CLR'}
  $taskModule=[HybridMount.MuiCheck]::LoadLibraryEx($taskMui,[IntPtr]::Zero,0x22);if($taskModule -eq [IntPtr]::Zero){throw 'MUI load failed'}
  try{for($taskI=0;$taskI -lt $taskNative.Count;$taskI++){
   $taskKey='Text_'+[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($taskNative[$taskI]))).Substring(0,12);$taskExpected=if($taskI -eq 0){$taskNative[0]}else{$taskTexts[$taskLanguage][$taskKey]}
   $taskBuffer=[Text.StringBuilder]::new(512);if([HybridMount.MuiCheck]::LoadString($taskModule,(1001+$taskI),$taskBuffer,512) -eq 0 -or $taskBuffer.ToString() -cne $taskExpected){throw "MUI value: $taskLanguage/$taskI"}
  }}finally{[void][HybridMount.MuiCheck]::FreeLibrary($taskModule)}
 }
}
Write-Host "Localization checked: $($taskCatalog.Count) keys x 3 languages / 8 native strings x 3"
