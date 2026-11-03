# 显式真实验收：读取既有配置的 DPAPI 凭据，唯一文件、独立本地根，不创建远端目录。
[CmdletBinding()]
param([string]$OriginalRoot='D:\wdavtest')
$ErrorActionPreference='Stop'
$repositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$binary=Join-Path $repositoryRoot 'HybridMount.Tests/bin/x64/Debug/net10.0-windows10.0.26100.0'
Add-Type -Path (Join-Path $binary 'HybridMount.Tests.dll')
[void][Runtime.InteropServices.NativeLibrary]::Load((Join-Path $binary 'runtimes/win-x64/native/e_sqlite3.dll'))
Push-Location $repositoryRoot
try {
    $area=Join-Path $repositoryRoot ('artifacts/validation/007-real-sync-'+[Guid]::NewGuid().ToString('N'))
    $report=[HybridMount.Tests.RemoteSyncLiveRunner]::RunOriginalAsync($OriginalRoot,$area,[Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $result=Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    Write-Output $report
    if($result.Phase -ne 'Completed' -or @($result.Cleanup | Where-Object State -ne 'Completed').Count){throw '真实验收或精确清理未完成，参见报告。'}
} finally {Pop-Location}
