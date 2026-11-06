# 只调用本产品的用户／会话管道，不连接网络；v2 文件命令返回异步任务 ID，支持查询和取消。
[CmdletBinding()]
param([ValidateSet('ping','showWindow','freeSpace','keepOnDevice','getOperation','cancelOperation','showSyncIssues')][string]$Operation='ping',[string[]]$Paths=@(),[string]$JobId)
$ErrorActionPreference='Stop'
$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$session=[Diagnostics.Process]::GetCurrentProcess().SessionId
$name="HybridMount.NET.v2.$sid.$session"
$id=[Guid]::NewGuid().ToString()
$json=@{version=2;requestId=$id;operation=$Operation;paths=@($Paths);jobId=$JobId} | ConvertTo-Json -Compress
$body=[Text.Encoding]::UTF8.GetBytes($json)
$pipe=[IO.Pipes.NamedPipeClientStream]::new('.', $name, [IO.Pipes.PipeDirection]::InOut, ([IO.Pipes.PipeOptions]::Asynchronous -bor [IO.Pipes.PipeOptions]::CurrentUserOnly))
$deadline=[Threading.CancellationTokenSource]::new(5000)
function Read-Bytes([byte[]]$buffer) {
    $offset=0
    while ($offset -lt $buffer.Length) {
        $count=$pipe.ReadAsync($buffer,$offset,$buffer.Length-$offset,$deadline.Token).GetAwaiter().GetResult()
        if ($count -eq 0) { throw 'IPC 响应被截断。' }
        $offset+=$count
    }
}
try {
    [void]$pipe.ConnectAsync($deadline.Token).GetAwaiter().GetResult()
    $header=[BitConverter]::GetBytes([uint32]$body.Length)
    [void]$pipe.WriteAsync($header,0,4,$deadline.Token).GetAwaiter().GetResult()
    [void]$pipe.WriteAsync($body,0,$body.Length,$deadline.Token).GetAwaiter().GetResult()
    [void]$pipe.FlushAsync($deadline.Token).GetAwaiter().GetResult()
    Read-Bytes $header
    $length=[BitConverter]::ToUInt32($header)
    if ($length -eq 0 -or $length -gt 1048576) { throw 'IPC 响应长度无效。' }
    $buffer=[byte[]]::new($length)
    Read-Bytes $buffer
    $response=[Text.Encoding]::UTF8.GetString($buffer) | ConvertFrom-Json
    if ($response.version -ne 2 -or $response.requestId -ne $id) { throw 'IPC 关联校验失败。' }
    $response
} finally { $pipe.Dispose(); $deadline.Dispose() }
