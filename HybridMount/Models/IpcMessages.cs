using HybridMount.Models.Mounting;
namespace HybridMount.Models;

/// <summary>版本化本用户请求，任务查询/取消使用 JobId，文件操作使用完整路径。</summary>
public sealed record IpcRequest(int Version,string RequestId,string Operation,string[] Paths,string? JobId=null);
/// <summary>文件操作只在入队后确认，实际结果通过任务快照查询。</summary>
public sealed record IpcResponse(int Version,string RequestId,string Status,string Message,string? JobId=null,MountOperationSnapshot? Job=null);
