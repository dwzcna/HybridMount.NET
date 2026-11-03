# 显式只读配额验收：仅加载公开入口，不构造授权头或访问私有反射。
[CmdletBinding()]
param([string]$OriginalRoot='D:\wdavtest')
$ErrorActionPreference='Stop'
$repositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$binary=Join-Path $repositoryRoot 'HybridMount.Tests/bin/x64/Debug/net10.0-windows10.0.26100.0'
Add-Type -Path (Join-Path $binary 'HybridMount.Tests.dll')
$area=Join-Path $repositoryRoot ('artifacts/validation/0082-real-quota-'+[Guid]::NewGuid().ToString('N'))
$report=[HybridMount.Tests.RemoteCapacityLiveRunner]::RunOriginalAsync($OriginalRoot,$area,[Threading.CancellationToken]::None).GetAwaiter().GetResult()
Get-Content -LiteralPath $report
