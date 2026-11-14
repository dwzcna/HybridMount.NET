namespace HybridMount.Models.Mounting;

/// <summary>只保存草稿摘要及展示快照；拒绝迟到测试，不保存密码或重新解释业务权限。</summary>
public sealed class ProtocolCapabilityDraftSession(FileSourceKind kind, string originalIdentity, long credentialRevision, FileSourceConfiguration? originalSource)
{
    private string? _pending, _tested;
    private readonly string _originalIdentity = originalIdentity;
    private string _lastIdentity = originalIdentity;
    private Guid? _liveSession;
    private ProtocolCapabilitySnapshot? _merged, _mergedTest, _mergedLive;
    private long _revision, _pendingRevision;
    private ProtocolCapabilitySnapshot? _test;
    /// <summary>每次输入变化推进草稿代次；即使随后改回原值，也不接纳旧测试。</summary>
    public long Observe(string identity)
    {
        if (_lastIdentity != identity)
        {
            _lastIdentity = identity; _revision++; _tested = null; _test = null;
        }
        return _revision;
    }
    /// <summary>输入控件在同步变更事件中调用；不依赖可能被合并的异步通知，保留单调代次。</summary>
    public void Invalidate()
    {
        _revision++;
        _tested = null;
        _test = null;
    }
    public void Begin(string identity) { _pendingRevision = Observe(identity); _pending = identity; }
    public void Cancel() => _pending = null;
    public bool Complete(string identity, ProtocolCapabilitySnapshot? snapshot)
    {
        Observe(identity);
        if (_pending is null || _pending != identity || _pendingRevision != _revision) { _pending = null; return false; }
        _pending = null; _tested = identity;
        _test = snapshot ?? new(kind, Guid.NewGuid(), DateTimeOffset.UtcNow, Array.Empty<ProtocolCapabilityEvidence>());
        return true;
    }
    public ProtocolCapabilitySnapshot? Select(string identity, MountSnapshot? mount)
    {
        Observe(identity);
        if (mount?.IsConnected == true && mount.ProtocolCapabilities is { } current)
        {
            if (_liveSession is { } before && before != current.SessionId) { _pending = null; _tested = null; _test = null; }
            _liveSession = current.SessionId;
        }
        var live = identity == _originalIdentity && mount?.Definition.CredentialRevision == credentialRevision && mount.Definition.Source == originalSource
            ? mount.ProtocolCapabilities : null;
        // 未改动的已连接表单持续展示当前会话；草稿测试不能冻结后续真实操作的证据。
        if (mount?.IsConnected == true && live is not null)
        {
            if (_tested == identity && _test is { } test && test.Evidence.Any(e => e.Id.StartsWith("sqs.", StringComparison.Ordinal)))
            {
                if (!ReferenceEquals(_mergedLive, live) || !ReferenceEquals(_mergedTest, test))
                {
                    var entries = live.Evidence.Concat(test.Evidence.Where(e => e.Id.StartsWith("sqs.", StringComparison.Ordinal)))
                        .GroupBy(e => (e.Id, e.Scope)).Select(group => group.OrderByDescending(e => e.ObservedUtc).First()).ToArray();
                    _mergedLive = live; _mergedTest = test; _merged = live with { Evidence = Array.AsReadOnly(entries) };
                }
                return _merged;
            }
            return live;
        }
        return _tested == identity ? _test : live;
    }
    public bool IsTest(string identity) => _tested == identity;
}
