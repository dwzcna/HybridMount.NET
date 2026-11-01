# 只处理已停止且本地根已移除的显式测试；公开入口验证范围与内容，不使用私有反射或更改安全设置。
[CmdletBinding()]
param([Parameter(Mandatory)][string]$WorkDirectory)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$validation=Join-Path $root 'artifacts/validation'
$work=[IO.Path]::GetFullPath($WorkDirectory)
if(-not $work.StartsWith($validation+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '恢复目录必须位于独立验证目录。'}
$binary=Join-Path $root 'HybridMount.Tests/bin/x64/Debug/net10.0-windows10.0.26100.0'
[void][Reflection.Assembly]::LoadFrom((Join-Path $binary 'HybridMount.Tests.dll'))
[void][Runtime.InteropServices.NativeLibrary]::Load((Join-Path $binary 'runtimes/win-x64/native/e_sqlite3.dll'))
$report=[HybridMount.Tests.LiveWebDavRecovery]::RecoverAsync($work,[Threading.CancellationToken]::None).GetAwaiter().GetResult()
Get-Content -LiteralPath $report
