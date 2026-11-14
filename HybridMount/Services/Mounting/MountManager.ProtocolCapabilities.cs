using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

public sealed partial class MountManager
{
    private void ObserveProtocolCapabilities(Runtime root)
    {
        if (root.Source is not { } source) return;
        lock (root.ConnectionSync)
        {
            root.ProtocolSource = source;
            root.ProtocolCredentialRevision = root.Definition.CredentialRevision;
            root.ProtocolSourceConfiguration = root.Definition.Source;
            root.LastProtocolCapabilities = source.ProtocolCapabilities;
        }
        root.ProtocolObserver = () =>
        {
            var snapshot = source.ProtocolCapabilities;
            lock (root.ConnectionSync)
            {
                if (!ReferenceEquals(root.Source, source) || !ReferenceEquals(root.ProtocolSource, source) || root.Definition.CredentialRevision != root.ProtocolCredentialRevision || IsStopping) return;
                Volatile.Write(ref root.LastProtocolCapabilities, snapshot);
            }
            if (Interlocked.Exchange(ref root.ProtocolPending, 1) == 0) _ = PublishProtocolCapabilitiesAsync(root, source);
        };
        source.ProtocolCapabilitiesChanged += root.ProtocolObserver;
    }
    private async Task PublishProtocolCapabilitiesAsync(Runtime root, IFileSource source)
    {
        ProtocolCapabilitySnapshot? published = null;
        try
        {
            await Task.Delay(250, _stop.Token).ConfigureAwait(false);
            if (ReferenceEquals(root.Source, source) && !IsStopping)
            {
                published = Volatile.Read(ref root.LastProtocolCapabilities);
                // 能力更新不唤醒需要SQL的业务展示整理。
                lock (_displayGate) Volatile.Write(ref _displayMounts, CaptureDisplay(_displayIssues));
                NotifyChanged();
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            Interlocked.Exchange(ref root.ProtocolPending, 0);
            if (published is not null && !ReferenceEquals(published, Volatile.Read(ref root.LastProtocolCapabilities)) && ReferenceEquals(root.Source, source) && !IsStopping)
                root.ProtocolObserver?.Invoke();
        }
    }
    private static void UnobserveProtocolCapabilities(Runtime root)
    {
        lock (root.ConnectionSync)
        {
            if (root.ProtocolSource is { } source && root.ProtocolObserver is { } observer)
            {
                Volatile.Write(ref root.LastProtocolCapabilities, source.ProtocolCapabilities);
                source.ProtocolCapabilitiesChanged -= observer;
            }
            root.ProtocolSource = null; root.ProtocolObserver = null;
        }
    }
    private static ProtocolCapabilitySnapshot? ReadProtocolCapabilities(Runtime root)
        => root.ProtocolCredentialRevision == root.Definition.CredentialRevision && root.ProtocolSourceConfiguration == root.Definition.Source ? Volatile.Read(ref root.LastProtocolCapabilities) : null;
}
