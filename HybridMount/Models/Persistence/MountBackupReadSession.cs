using System.Security.Cryptography;
using HybridMount.Models.Mounting;

namespace HybridMount.Models.Persistence;

/// <summary>持有一次有界读取的备份快照；密码重试不重开文件。释放不阻塞 UI，活动解密结束后清零字节。</summary>
internal sealed class MountBackupReadSession : IDisposable
{
    private readonly object _gate = new();
    private byte[]? _snapshot;
    private int _readers;
    private bool _disposed;
    internal bool RequiresPassword { get; }
    internal MountBackupReadSession(byte[] snapshot, bool requiresPassword)
    { _snapshot = snapshot; RequiresPassword = requiresPassword; }

    /// <summary>调用方在后台解密；短锁只保护快照租用，取消、错误及释放都归还租用。</summary>
    internal MountConfigurationBackup Decode(string password, CancellationToken token = default)
    {
        byte[] bytes;
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); bytes = _snapshot!; ++_readers; }
        try { return MountBackupCodec.Decode(bytes, password, token); }
        finally { lock (_gate) { --_readers; if (_disposed && _readers == 0) Clear(); } }
    }
    /// <summary>幂等释放；在途后台操作拥有自己的租用，不能在它解析时改写缓冲。</summary>
    public void Dispose()
    { lock (_gate) { _disposed = true; if (_readers == 0) Clear(); } }
    private void Clear()
    { if (_snapshot is not null) { CryptographicOperations.ZeroMemory(_snapshot); _snapshot = null; } }
}
