using System.Collections.ObjectModel;

namespace HybridMount.Models.Mounting;

/// <summary>展示证据状态；声明不等于实测。此枚举不用于授予任何业务操作权限。</summary>
public enum ProtocolCapabilityStatus { Unknown, Advertised, Supported, Unsupported }
/// <summary>固定诊断原因，不包含响应正文、URL、会话标识或认证材料。</summary>
public enum ProtocolCapabilityReason { None, Declared, Permission, Uncertain, RangeIgnored, ConditionIgnored, MixedScopes, QuotaMissing }
/// <summary>单个接口在精确相对路径／目录中的证据；空路径表示配置根，不代表整个子树。</summary>
public sealed record ProtocolCapabilityEvidence(string Id, ProtocolCapabilityStatus Status, string Scope, DateTimeOffset ObservedUtc, ProtocolCapabilityReason Reason = ProtocolCapabilityReason.None);
/// <summary>连接拥有的不可变有界内存快照；重连更换SessionId，不序列化到配置、IPC或业务数据库。</summary>
public sealed record ProtocolCapabilitySnapshot(FileSourceKind Kind, Guid SessionId, DateTimeOffset ObservedUtc, IReadOnlyList<ProtocolCapabilityEvidence> Evidence);
/// <summary>一个接口的范围汇总；不同目录实测不一致为未知，同范围实测始终优先于声明。</summary>
public sealed record ProtocolCapabilitySummary(ProtocolCapabilityStatus Status, ProtocolCapabilityReason Reason, IReadOnlyList<ProtocolCapabilityEvidence> Evidence)
{
    public static ProtocolCapabilitySummary Read(ProtocolCapabilitySnapshot snapshot, string id)
    {
        var all = snapshot.Evidence.Where(item => item.Id == id).ToArray();
        var tested = all.Where(item => item.Status != ProtocolCapabilityStatus.Advertised).ToArray();
        if (tested.Length == 0) return new(all.Length == 0 ? ProtocolCapabilityStatus.Unknown : ProtocolCapabilityStatus.Advertised,
            all.Length == 0 ? ProtocolCapabilityReason.None : ProtocolCapabilityReason.Declared, Array.AsReadOnly(all));
        var states = tested.Select(item => item.Status).Distinct().ToArray();
        return states.Length == 1 ? new(states[0], tested.Last().Reason, Array.AsReadOnly(tested)) :
            new(ProtocolCapabilityStatus.Unknown, ProtocolCapabilityReason.MixedScopes, Array.AsReadOnly(tested));
    }
}

/// <summary>协议线程记录已有请求的结果；256条证据上限，重复状态不分配快照，锁外通知且观察者不能改变业务结果。</summary>
internal sealed class ProtocolCapabilityTracker(FileSourceKind kind)
{
    private const int Capacity = 256;
    private readonly object _gate = new();
    private readonly Dictionary<(string Id, string Scope), ProtocolCapabilityEvidence> _evidence = [];
    private ProtocolCapabilitySnapshot _snapshot = new(kind, Guid.NewGuid(), DateTimeOffset.UtcNow, Array.Empty<ProtocolCapabilityEvidence>());
    internal ProtocolCapabilitySnapshot Snapshot => Volatile.Read(ref _snapshot);
    internal event Action? Changed;

    internal void Record(string id, ProtocolCapabilityStatus status, string? scope = null, ProtocolCapabilityReason reason = ProtocolCapabilityReason.None)
    {
        scope ??= "";
        if (scope.Length > 4096 || scope.Any(char.IsControl)) return;
        lock (_gate)
        {
            var key = (id, scope);
            if (_evidence.TryGetValue(key, out var before))
            {
                if (status == ProtocolCapabilityStatus.Advertised && before.Status != ProtocolCapabilityStatus.Advertised) return;
                if (before.Status == status && before.Reason == reason) return;
            }
            if (!_evidence.ContainsKey(key) && _evidence.Count == Capacity)
                _evidence.Remove(_evidence.MinBy(pair => pair.Value.ObservedUtc).Key);
            var now = DateTimeOffset.UtcNow;
            _evidence[key] = new(id, status, scope, now, reason);
            Volatile.Write(ref _snapshot, new(kind, _snapshot.SessionId, now, Array.AsReadOnly(_evidence.Values.ToArray())));
        }
        if (Changed is { } observers) foreach (Action observer in observers.GetInvocationList())
            try { observer(); } catch { /* 展示观察者不改写任何请求的实际结果。 */ }
    }
    internal void ClearPrefix(string prefix)
    {
        lock (_gate)
        {
            var keys = _evidence.Keys.Where(key => key.Id.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            if (keys.Length == 0) return;
            foreach (var key in keys) _evidence.Remove(key);
            Volatile.Write(ref _snapshot, new(kind, _snapshot.SessionId, DateTimeOffset.UtcNow, Array.AsReadOnly(_evidence.Values.ToArray())));
        }
        if (Changed is { } observers) foreach (Action observer in observers.GetInvocationList()) try { observer(); } catch { }
    }
    internal void Verified(string id, CapabilitySupport status, string scope) => Record(id, status switch
    {
        CapabilitySupport.Supported => ProtocolCapabilityStatus.Supported,
        CapabilitySupport.Unsupported => ProtocolCapabilityStatus.Unsupported,
        _ => ProtocolCapabilityStatus.Unknown
    }, scope, status == CapabilitySupport.Unsupported ? ProtocolCapabilityReason.ConditionIgnored : ProtocolCapabilityReason.None);
}
