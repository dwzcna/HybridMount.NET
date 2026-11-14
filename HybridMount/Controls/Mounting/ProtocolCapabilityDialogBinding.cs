using System.Security.Cryptography;
using System.Text.Json;
using HybridMount.Models.Mounting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HybridMount.Controls.Mounting;

/// <summary>UI线程协调草稿摘要、连接代次及测试结果；输入变化只失效展示，不发请求或读库，关闭解除全部控件订阅。</summary>
internal sealed class ProtocolCapabilityDialogBinding : IDisposable
{
    private readonly ContentDialog _owner;
    private readonly ProtocolCapabilitiesView _view;
    private readonly FileSourceKind _kind;
    private readonly Func<string> _identity;
    private readonly Func<MountSnapshot?> _mount;
    private readonly Func<string>? _serverIdentity;
    private readonly ProtocolCapabilityDraftSession _session;
    private readonly List<Action> _unsubscribes = [];
    private bool _disposed, _watching;
    private string? _pendingServer, _pendingQueue, _lastServer;
    private long _serverRevision, _pendingServerRevision, _pendingQueueRevision;

    internal ProtocolCapabilityDialogBinding(ContentDialog owner, ProtocolCapabilitiesView view, FileSourceKind kind,
        Func<string> identity, Func<MountSnapshot?> mount, Func<string>? serverIdentity = null)
    {
        _owner = owner; _view = view; _kind = kind; _identity = identity; _mount = mount; _serverIdentity = serverIdentity;
        var current = mount();
        _session = new(kind, SafeIdentity(), current?.Definition.CredentialRevision ?? 0, current?.Definition.Source);
        owner.Loaded += Loaded; owner.Closed += Closed;
        _lastServer = SafeServerIdentity();
        Refresh();
    }
    /// <summary>凭据只用于一次内存摘要；UTF8临时缓冲立即清零，不在快照中保存原文。</summary>
    internal static string Fingerprint(FileSourceConfiguration source, SourceCredentials credentials, object? additional = null)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { source, credentials, additional });
        try { return Convert.ToHexString(SHA256.HashData(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private string SafeIdentity()
    {
        try { return _identity(); }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException or OverflowException or FormatException) { return "invalid"; }
    }
    private string? SafeServerIdentity()
    {
        try { return _serverIdentity?.Invoke(); }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException or OverflowException or FormatException) { return "invalid"; }
    }
    internal void BeginTest() { _session.Begin(SafeIdentity()); _pendingServer = SafeServerIdentity(); _pendingServerRevision = _serverRevision; }
    internal void CancelTest() => _session.Cancel();
    internal void CompleteTest(ProtocolCapabilitySnapshot? snapshot, bool serverDiscovery = false)
    {
        if (_disposed) return;
        // 首次群晖登录只发现服务器共享，界面可据此初始化共享选择；不把服务端证据标为新目录已验证。
        if (serverDiscovery && snapshot is not null && snapshot.Evidence.All(e => e.Scope == "@server") && SafeServerIdentity() == _pendingServer && _serverRevision == _pendingServerRevision)
            _session.Begin(SafeIdentity());
        _session.Complete(SafeIdentity(), snapshot); Refresh();
    }
    internal void BeginQueueTest() { _pendingQueue = SafeIdentity(); _pendingQueueRevision = _session.Observe(_pendingQueue); }
    internal void CompleteQueueTest(ProtocolCapabilitySnapshot? queue)
    {
        if (_disposed || queue is null) return;
        var identity = SafeIdentity(); if (identity != _pendingQueue || _session.Observe(identity) != _pendingQueueRevision) return; _pendingQueue = null; var current = _session.Select(identity, _mount());
        var evidence = (current?.Evidence.Where(e => !e.Id.StartsWith("sqs.", StringComparison.Ordinal)) ?? []).Concat(queue.Evidence).ToArray();
        _session.Begin(identity); _session.Complete(identity, queue with { Evidence = Array.AsReadOnly(evidence) }); Refresh();
    }
    internal void Refresh()
    {
        if (_disposed) return;
        var server = SafeServerIdentity(); if (server != _lastServer) { _lastServer = server; _serverRevision++; }
        var current = _mount(); var identity = SafeIdentity(); var selected = _session.Select(identity, current);
        _view.Show(_kind, selected, selected is not null && !_session.IsTest(identity) && current?.IsConnected != true, current?.Definition.Sqs?.Enabled == true);
    }
    private void Loaded(object sender, RoutedEventArgs args)
    {
        if (_watching) return;
        _watching = true; Watch(_owner.Content); Refresh();
    }
    /// <summary>同步推进代次并立即失效展示；TextChanged可能合并，不能单靠摘要判断改回原值的输入。</summary>
    private void Invalidate(bool server)
    {
        _session.Invalidate();
        _pendingQueue = null;
        if (server) _serverRevision++;
        _view.Show(_kind, null, false, _mount()?.Definition.Sqs?.Enabled == true);
    }
    private void Watch(object? item, bool relevant = false)
    {
        if (item is FrameworkElement element)
        {
            if (element.Name is "ConnectionFields" or "SqsFields") relevant = true;
            if (element.Name is "NameField" or "PathField" or "AutoConnectField" or "OtpField") relevant = false;
        }
        var server = item is FrameworkElement fieldElement && fieldElement.Name is "EndpointField" or "UserField" or "PasswordField" or "HttpField";
        switch (item)
        {
            case TextBox field when relevant:
                global::Windows.Foundation.TypedEventHandler<TextBox, TextBoxBeforeTextChangingEventArgs> before = (sender, args) =>
                {
                    if (args.NewText != sender.Text) Invalidate(server);
                };
                TextChangedEventHandler text = (_, _) => Refresh();
                field.BeforeTextChanging += before; field.TextChanged += text;
                _unsubscribes.Add(() => { field.BeforeTextChanging -= before; field.TextChanged -= text; });
                break;
            case PasswordBox field when relevant:
                global::Windows.Foundation.TypedEventHandler<PasswordBox, PasswordBoxPasswordChangingEventArgs> changing = (_, args) =>
                {
                    if (args.IsContentChanging) Invalidate(server);
                };
                RoutedEventHandler password = (_, _) => Refresh();
                field.PasswordChanging += changing; field.PasswordChanged += password;
                _unsubscribes.Add(() => { field.PasswordChanging -= changing; field.PasswordChanged -= password; });
                break;
            case ComboBox field when relevant:
                SelectionChangedEventHandler selection = (_, _) => { Invalidate(server); Refresh(); };
                field.SelectionChanged += selection; _unsubscribes.Add(() => field.SelectionChanged -= selection);
                break;
            case ToggleSwitch field when relevant:
                RoutedEventHandler toggled = (_, _) => { Invalidate(server); Refresh(); };
                field.Toggled += toggled; _unsubscribes.Add(() => field.Toggled -= toggled);
                break;
            case CheckBox field when relevant:
                RoutedEventHandler check = (_, _) => { Invalidate(server); Refresh(); };
                field.Checked += check; field.Unchecked += check;
                _unsubscribes.Add(() => { field.Checked -= check; field.Unchecked -= check; });
                break;
        }
        switch (item)
        {
            case Panel panel: foreach (var child in panel.Children) Watch(child, relevant); break;
            case ScrollViewer scroll: Watch(scroll.Content, relevant); break;
            case Expander expander: Watch(expander.Content, relevant); break;
            case UserControl user: Watch(user.Content, relevant); break;
            case ContentControl content: Watch(content.Content, relevant); break;
        }
    }
    private void Closed(ContentDialog sender, ContentDialogClosedEventArgs args) => Dispose();
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _session.Cancel(); _owner.Loaded -= Loaded; _owner.Closed -= Closed;
        foreach (var unsubscribe in _unsubscribes) unsubscribe(); _unsubscribes.Clear();
    }
}
