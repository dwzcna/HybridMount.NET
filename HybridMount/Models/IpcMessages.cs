namespace HybridMount.Models;

/// <summary>本机扩展请求；首版只接纳占位操作，不执行文件变更。</summary>
/// <param name="Version">传输协议版本。</param><param name="RequestId">请求关联标识。</param><param name="Operation">操作名称。</param><param name="Paths">完整文件系统路径集合。</param>
public sealed record IpcRequest(int Version, string RequestId, string Operation, string[] Paths);

/// <summary>占位操作或窗口激活的明确响应。</summary>
/// <param name="Version">响应协议版本。</param><param name="RequestId">原请求标识。</param><param name="Status">ready、accepted、notImplemented 或 invalidRequest。</param><param name="Message">可显示的产品说明。</param>
public sealed record IpcResponse(int Version, string RequestId, string Status, string Message);
