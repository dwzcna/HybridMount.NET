# 显式运行真实 WebDAV 长时功能测试；普通构建／单元测试不会自动访问网络。
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Endpoint,
    [Parameter(Mandatory)][string]$WorkDirectory,
    [Parameter(Mandatory)][Guid]$CredentialId,
    [ValidateRange(60,180)][int]$Minutes=65,
    [ValidateSet('Debug','Release')][string]$Configuration='Debug',
    [switch]$Extended,
    [switch]$Focused
)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$work=[IO.Path]::GetFullPath($WorkDirectory)
$validation=Join-Path $root 'artifacts/validation'
if(-not $work.StartsWith($validation+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '测试工作目录必须位于 artifacts/validation 的独立子目录。'}
$binary=Join-Path $root "HybridMount.Tests/bin/x64/$Configuration/net10.0-windows10.0.26100.0"
if(-not (Test-Path -LiteralPath (Join-Path $binary 'HybridMount.Tests.dll'))){throw '请先编译对应 x64 测试工程。'}
# 固定本轮运行程序集副本，避免长时验证锁住工作区的增量编译输出。
$runtime=Join-Path $work 'runtime'
if(Test-Path -LiteralPath $runtime){throw '本轮 runtime 副本已经存在，请使用新的工作目录或先检查旧运行状态。'}
[void][IO.Directory]::CreateDirectory($runtime)
Get-ChildItem -LiteralPath $binary|Copy-Item -Destination $runtime -Recurse
$assembly=Join-Path $runtime 'HybridMount.Tests.dll'
[void][Reflection.Assembly]::LoadFrom($assembly)
[void][Runtime.InteropServices.NativeLibrary]::Load((Join-Path $runtime 'runtimes/win-x64/native/e_sqlite3.dll'))
# 在独立工作目录写入 stop.request 可正常取消本轮，仍由测试器完成精确清理。
$stopping=[Threading.CancellationTokenSource]::new()
try {
    $running=if($Focused) {
        [HybridMount.Tests.LiveWebDavRunner]::RunFocusedAsync($Endpoint,$work,$CredentialId,$stopping.Token)
    } elseif($Extended) {
        [HybridMount.Tests.LiveWebDavRunner]::RunExtendedAsync($Endpoint,$work,$CredentialId,$Minutes,$stopping.Token)
    } else { [HybridMount.Tests.LiveWebDavRunner]::RunAsync($Endpoint,$work,$CredentialId,$Minutes,$stopping.Token) }
    while(-not $running.IsCompleted) {
        if(-not $stopping.IsCancellationRequested -and (Test-Path -LiteralPath (Join-Path $work 'stop.request'))) { $stopping.CancelAsync().GetAwaiter().GetResult() }
        [Threading.Thread]::Sleep(500)
    }
    $report=$running.GetAwaiter().GetResult()
} finally { $stopping.Dispose() }
$result=Get-Content -LiteralPath $report -Raw|ConvertFrom-Json
$result|Select-Object Version,Endpoint,Phase,StartedUtc,EndedUtc,ElapsedSeconds,RequestedMinutes,Round,Passed,Limited,Failed|Format-List
if($Focused) {
    if($result.Phase -ne 'FocusedCompleted' -or $result.Failed -ne 0){throw "聚焦复测未通过，见 $report"}
} elseif($result.Phase -ne 'Completed' -or $result.ElapsedSeconds -lt $Minutes*60 -or $result.Failed -ne 0){throw "真实 WebDAV 测试尚未完整通过，见 $report"}
