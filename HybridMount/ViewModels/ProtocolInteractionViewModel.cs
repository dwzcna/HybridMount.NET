using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HybridMount.Models.UI;

namespace HybridMount.ViewModels;

/// <summary>三协议共用业务可用性；命令只通过纯回调调用视图适配，异步任务由本模型持有并观察。</summary>
public sealed class ProtocolInteractionViewModel : ObservableObject, IDisposable
{
    private ProtocolInteractionState _state = new(Stopped: true);
    private Func<string, object?, Task>? _handler;
    private Action<Exception>? _error;
    private readonly Dictionary<string, IRelayCommand> _commands = [];
    private readonly Dictionary<Guid, (string Action, Task Work)> _active = [];
    private bool _disposed;
    public ProtocolInteractionState State => _state;
    public bool CanEditDraft => _state.CanEditDraft;
    public bool CanEditConnection => _state.CanEditConnection;
    public bool CanEditSettings => _state.CanEditSettings;
    public bool CanEditSqs => _state.CanEditSqs;
    public bool CanEditRootOptions => _state.CanEditRootOptions;
    public bool CanEditCapacityValue => _state.CanEditCapacityValue;
    public bool CanToggleSettings => _state.CanToggleSettings;
    public bool CanEditThumbnails => _state.CanEditThumbnails;
    public bool CanEditS3Secrets => _state.CanEditS3Secrets;
    public bool CanEditSqsSecrets => _state.CanEditSqsSecrets;
    public bool CanSubmit => _state.CanSubmit;
    public bool CanTest => _state.CanTest;
    public Task WhenIdle => Task.WhenAll(_active.Values.Select(value => value.Work));
    public IRelayCommand ChooseFolderCommand => _commands["ChooseFolder"];
    public IRelayCommand AddExclusionRuleCommand => _commands["AddExclusionRule"];
    public IRelayCommand AddDefaultExclusionRulesCommand => _commands["AddDefaultExclusionRules"];
    public IRelayCommand SaveExclusionsCommand => _commands["SaveExclusions"];
    public IRelayCommand SaveCapacityCommand => _commands["SaveCapacity"];
    public IRelayCommand RecheckCapacityCommand => _commands["RecheckCapacity"];
    public IRelayCommand SaveIntervalsCommand => _commands["SaveIntervals"];
    public IRelayCommand FetchCertificateCommand => _commands["FetchCertificate"];
    public IRelayCommand ClearCertificateTrustCommand => _commands["ClearCertificateTrust"];
    public IRelayCommand TrustCertificateCommand => _commands["TrustCertificate"];
    public IRelayCommand ClearPinCommand => _commands["ClearPin"];
    public IRelayCommand TestSqsCommand => _commands["TestSqs"];
    public IRelayCommand SaveSqsCommand => _commands["SaveSqs"];
    public ProtocolInteractionViewModel()
    {
        foreach (var action in new[] { "ChooseFolder", "AddExclusionRule", "AddDefaultExclusionRules", "SaveExclusions", "SaveCapacity", "RecheckCapacity", "SaveIntervals", "FetchCertificate", "ClearCertificateTrust", "TrustCertificate", "ClearPin", "TestSqs", "SaveSqs" })
            _commands[action] = new RelayCommand(() => Execute(action), () => Can(action, _state));
    }
    public void SetHandler(Func<string, object?, Task> handler, Action<Exception> error) { _handler = handler; _error = error; }
    /// <summary>UI线程按值发布，只通知真正变化的属性与CanExecute，不经过进度节流。</summary>
    public void Publish(ProtocolInteractionState state)
    {
        if (_disposed || _state == state) return;
        var before = Values(_state); var old = _state; _state = state; var after = Values(state);
        for (var i = 0; i < before.Length; i++) if (before[i].Value != after[i].Value) OnPropertyChanged(after[i].Name);
        foreach (var pair in _commands) if (Can(pair.Key, old) != Can(pair.Key, state)) pair.Value.NotifyCanExecuteChanged();
    }
    private bool Can(string action, ProtocolInteractionState state) => !_disposed && (action switch
    {
        "ChooseFolder" => state.CanChooseFolder,
        "AddExclusionRule" or "AddDefaultExclusionRules" => state.CanEditSettings,
        "SaveExclusions" or "SaveCapacity" or "SaveIntervals" or "SaveSqs" => state.Existing && state.CanChangeSettings,
        "RecheckCapacity" => state.CapacityAvailable && state.CanChangeSettings,
        "FetchCertificate" => state.Https && state.CanEditConnection,
        "TrustCertificate" => state.HasCandidate && state.CanEditConnection,
        "ClearCertificateTrust" or "ClearPin" => state.HasTrustedCertificate && state.CanEditConnection,
        "TestSqs" => state.Existing && !state.Stopped && !state.Submitting && !state.Testing && !state.Probing && !state.SqsSaving && (!state.SqsTesting || !state.SqsCanceling),
        _ => false
    });
    private void Execute(string action)
    {
        if (!Can(action, _state) || _handler is null) return;
        if (_active.Values.Any(value => value.Action == action) && !(action == "TestSqs" && _state.SqsTesting)) return;
        var owner = Guid.NewGuid();
        _active[owner] = (action, Task.CompletedTask);
        var work = RunAsync(owner, action);
        if (_active.ContainsKey(owner)) _active[owner] = (action, work);
    }
    private async Task RunAsync(Guid owner, string action)
    {
        try { if (_handler is { } handler) await handler(action, null); }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!_disposed) _error?.Invoke(error); }
        finally { _active.Remove(owner); }
    }
    private static (string Name, bool Value)[] Values(ProtocolInteractionState s) =>
    [
        (nameof(CanEditDraft),s.CanEditDraft),(nameof(CanEditConnection),s.CanEditConnection),
        (nameof(CanEditSettings),s.CanEditSettings),(nameof(CanEditSqs),s.CanEditSqs),
        (nameof(CanEditRootOptions),s.CanEditRootOptions),(nameof(CanEditCapacityValue),s.CanEditCapacityValue),
        (nameof(CanToggleSettings),s.CanToggleSettings),(nameof(CanEditThumbnails),s.CanEditThumbnails),(nameof(CanEditS3Secrets),s.CanEditS3Secrets),(nameof(CanEditSqsSecrets),s.CanEditSqsSecrets),
        (nameof(CanSubmit),s.CanSubmit),(nameof(CanTest),s.CanTest)
    ];
    public void Dispose() { if (_disposed) return; Publish(_state with { Stopped = true }); _disposed = true; _handler = null; _error = null; }
}
