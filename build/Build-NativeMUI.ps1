# 以相同架构生成无代码的独立语言资源模块，保持四工程边界。
[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory,[Parameter(Mandatory)][ValidateSet('x64','ARM64')][string]$Platform)
$ErrorActionPreference='Stop'
$taskOutput=[IO.Path]::GetFullPath($OutputDirectory)
$taskRc=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin\10.0.26100.0\x64\rc.exe'
$taskVs=& (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe') -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$taskTools=Get-ChildItem -LiteralPath (Join-Path $taskVs 'VC\Tools\MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$taskLink=Join-Path $taskTools.FullName 'bin\Hostx64\x64\link.exe'
foreach($taskLanguage in @('zh-CN','en-US','zh-HK')){
 $taskDirectory=Join-Path $taskOutput $taskLanguage
 [IO.Directory]::CreateDirectory($taskDirectory)|Out-Null
 $taskSource=Join-Path $PSScriptRoot "..\HybridMount.Ext\Strings\$taskLanguage\NativeStrings.rc"
 $taskRes=Join-Path $taskDirectory 'NativeStrings.res'
 # RC不依赖调用shell的INCLUDE环境，仅需语言字符串。
 & $taskRc /nologo "/fo$taskRes" $taskSource
 if($LASTEXITCODE -ne 0){throw "MUI rc failed: $taskLanguage"}
 & $taskLink /nologo /DLL /NOENTRY "/MACHINE:$Platform" "/OUT:$taskDirectory\HybridMount.Ext.dll.mui" $taskRes
 if($LASTEXITCODE -ne 0){throw "MUI link failed: $taskLanguage"}
}
