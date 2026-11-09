namespace HybridMount.Models.Mounting.Windows;

/// <summary>通过原生运行时读取系统状态容器；原生端校验类型及向量，不触发媒体处理或正文水合。</summary>
internal static class CloudCustomStateReader
{
    internal static ShellCustomState Read(string path)=>NativeRuntimeProtocol.Read<ShellCustomState>(NativeRuntimeClient.Get().Invoke("readCustomState",new{path}));
}
