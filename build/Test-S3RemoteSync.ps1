# 显式真实 S3/R2 验收：只读原配置，唯一文件、独立 NTFS 根，精确清理。
[CmdletBinding()]
param([string]$OriginalRoot='D:\s3test')
$ErrorActionPreference='Stop'
$repositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$binary=Join-Path $repositoryRoot 'HybridMount.Tests/bin/x64/Debug/net10.0-windows10.0.26100.0'
Add-Type -Path (Join-Path $binary 'HybridMount.Tests.dll')
[void][Runtime.InteropServices.NativeLibrary]::Load((Join-Path $binary 'runtimes/win-x64/native/e_sqlite3.dll'))
Push-Location $repositoryRoot
try {
    $area=Join-Path $repositoryRoot ('artifacts/validation/008-real-s3-'+[Guid]::NewGuid().ToString('N'))
    $report=[HybridMount.Tests.S3RemoteSyncLiveRunner]::RunOriginalAsync($OriginalRoot,$area,[Threading.CancellationToken]::None).GetAwaiter().GetResult()
    Write-Output $report
    $result=Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    if($result.Phase -ne 'Completed' -or @($result.Cleanup | Where-Object State -ne 'Completed').Count){throw '真实验收或清理未完成，参见报告。'}
} finally {Pop-Location}
