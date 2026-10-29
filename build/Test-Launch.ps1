# 通过进程元数据和公开 IPC 验证启动；不自动点击桌面，不代替视觉验收。
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ExecutablePath,[ValidateSet('Packaged','Unpackaged')][string]$Deployment='Unpackaged')
$ErrorActionPreference='Stop'
$executable=[IO.Path]::GetFullPath($ExecutablePath)
if (-not (Test-Path -LiteralPath $executable)) { throw '找不到应用可执行文件。' }
$evidence=[Collections.Generic.List[string]]::new()
$evidence.Add("UTC: $([DateTime]::UtcNow.ToString('O'))")
function Send-Request([string]$operation,[string[]]$paths=@()) { & (Join-Path $PSScriptRoot 'Invoke-Request.ps1') -Operation $operation -Paths $paths }
if (Get-Process -Name 'HybridMount.NET' -ErrorAction SilentlyContinue) { throw '请先退出正在运行的 HybridMount.NET，避免影响已有实例。' }
$primary=Start-Process -FilePath $executable -ArgumentList '--tray' -WindowStyle Hidden -PassThru
try {
    $ready=Send-Request 'ping'
    if ($ready.status -ne 'ready') { throw '主实例未就绪。' }
    $primary.Refresh()
    if ($primary.HasExited -or $primary.MainWindowHandle -ne 0) { throw '静默启动未保持无主窗口状态。' }
    $evidence.Add('PASS: 静默启动已运行且没有可见主窗口。')
    $secondary=Start-Process -FilePath $executable -ArgumentList '--tray' -WindowStyle Hidden -PassThru
    if (-not $secondary.WaitForExit(10000)) { throw '静默第二实例未退出。' }
    $primary.Refresh()
    if ($primary.MainWindowHandle -ne 0) { throw '静默重复启动激活了主窗口。' }
    $evidence.Add('PASS: 静默重复启动退出且未激活主窗口。')
    $secondary=Start-Process -FilePath $executable -WindowStyle Hidden -PassThru
    if (-not $secondary.WaitForExit(10000)) { throw '正常第二实例未退出。' }
    $limit=[DateTime]::UtcNow.AddSeconds(3)
    do { $primary.Refresh(); if ($primary.MainWindowHandle -ne 0) { break }; Start-Sleep -Milliseconds 50 } while ([DateTime]::UtcNow -lt $limit)
    if ($primary.MainWindowHandle -eq 0) { throw '正常重复启动未恢复主窗口。' }
    $evidence.Add('PASS: 正常重复启动退出且恢复了既有主窗口。')
    $testDirectory=Join-Path ([IO.Path]::GetTempPath()) ('HybridMount.LaunchTest.'+[Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($testDirectory) | Out-Null
    $testFile=Join-Path $testDirectory '占位测试.txt'
    try {
        [IO.File]::WriteAllText($testFile,'original')
        foreach ($operation in @('freeSpace','keepOnDevice')) {
            $response=Send-Request $operation @($testFile)
            if ($response.status -ne 'notImplemented') { throw '占位操作未明确报告尚未实现。' }
            if ([IO.File]::ReadAllText($testFile) -ne 'original') { throw '占位操作改动了测试文件。' }
        }
        $evidence.Add('PASS: 实际 WinUI 进程中的两个占位请求返回尚未实现，测试文件未改变。')
    } finally { if (Test-Path -LiteralPath $testDirectory) { Remove-Item -LiteralPath $testFile; Remove-Item -LiteralPath $testDirectory } }
    $evidence.Add("主实例 PID: $($primary.Id)，保留运行以便手工验收。")
} catch { $evidence.Add("FAIL: $($_.Exception.Message)"); throw }
finally {
    $validationPath=Join-Path $PSScriptRoot "..\artifacts\validation\launch-x64-$Deployment.txt"
    $evidence | Set-Content -LiteralPath $validationPath -Encoding utf8
    $evidence
}
