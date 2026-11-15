using HybridMount.Models.Mounting;
namespace HybridMount.Commons.Scheduling;

/// <summary>只传递当前逻辑任务；不拥有调度器实现、线程或执行位，作用域恢复仍由原所有者负责。</summary>
internal static class MountTaskContext
{
    internal sealed record ScopeState(MountQueueBinding Binding,MountingTask? Task);
    private static readonly AsyncLocal<ScopeState?> Ambient=new();
    internal static ScopeState? Current {get=>Ambient.Value;set=>Ambient.Value=value;}
    internal static MountQueueBinding? CurrentBinding=>Current?.Binding;
    internal static MountingTask? CurrentTask=>Current?.Task;
}
