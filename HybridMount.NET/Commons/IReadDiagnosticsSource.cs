using HybridMount.Models.Mounting;
namespace HybridMount.Commons;

/// <summary>可选的只读诊断事件，不改变文件源的读取能力、版本或取消语义。</summary>
public interface IReadDiagnosticsSource
{
    event Action<SourceReadDiagnostic>? ReadDiagnostic;
}
