using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

/// <summary>便携加密备份 v5（兼容读取 v1/v2/v3/v4）；只使用 .NET 密码学，工作由宿主在后台线程执行。</summary>
/// <remarks>文件为 44 字节认证头、16 字节 GCM 标签及密文。头依次为 HMCONFIG、LE 版本、盐、nonce、LE 密文长度。</remarks>
public static class MountBackupCodec
{
    public const int MaximumFileBytes = 8 * 1024 * 1024;
    public const int MaximumItems = 1000;
    private const int HeaderBytes = 44, TagBytes = 16, PayloadOffset = HeaderBytes + TagBytes;
    private const int Iterations = 600_000;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<FileSourceKind>(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    /// <summary>在内存中加密整个载荷；不修剪密码，取消后不返回可写入结果。</summary>
    public static byte[] Encode(MountConfigurationBackup backup, string password, CancellationToken token = default)
    {
        ValidatePassword(password); ValidateDocument(backup); token.ThrowIfCancellationRequested();
        byte[]? plain = null, passwordBytes = null, key = null;
        try
        {
            plain = backup.SchemaVersion==1?
                JsonSerializer.SerializeToUtf8Bytes(new LegacyBackup(1,backup.ExportedUtc,backup.Mounts.Select(x=>new LegacyItem(x.Name,x.LocalPath,x.Source,x.Credentials,x.AutoConnect,x.ThumbnailsEnabled)).ToArray()),Json):
                backup.SchemaVersion==2?JsonSerializer.SerializeToUtf8Bytes(new VersionTwoBackup(2,backup.ExportedUtc,backup.Mounts.Select(x=>new VersionTwoItem(x.Name,x.LocalPath,x.Source,x.Credentials,x.AutoConnect,x.ThumbnailsEnabled,x.RemoteAutoDetectEnabled,x.RemoteIncrementalSeconds,x.RemoteFullSeconds)).ToArray()),Json):
                backup.SchemaVersion==3?JsonSerializer.SerializeToUtf8Bytes(new VersionThreeBackup(3,backup.ExportedUtc,backup.Mounts.Select(x=>new VersionThreeItem(x.Name,x.LocalPath,x.Source,x.Credentials,x.AutoConnect,x.ThumbnailsEnabled,x.RemoteAutoDetectEnabled,x.RemoteIncrementalSeconds,x.RemoteFullSeconds,x.Sqs,x.SqsCredentials)).ToArray()),Json):
                backup.SchemaVersion==4?JsonSerializer.SerializeToUtf8Bytes(new VersionFourBackup(4,backup.ExportedUtc,backup.Mounts.Select(x=>new VersionFourItem(x.Name,x.LocalPath,x.Source,x.Credentials,x.AutoConnect,x.ThumbnailsEnabled,x.RemoteAutoDetectEnabled,x.RemoteIncrementalSeconds,x.RemoteFullSeconds,x.Sqs,x.SqsCredentials,x.CapacityAutoDetectEnabled,x.MaximumCapacityBytes)).ToArray()),Json):
                JsonSerializer.SerializeToUtf8Bytes(backup, Json);
            if (plain.Length > MaximumFileBytes - PayloadOffset) throw new InvalidDataException("配置备份超过 8 MiB 限制。");
            var output = new byte[PayloadOffset + plain.Length]; "HMCONFIG"u8.CopyTo(output);
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(8, 4), backup.SchemaVersion);
            RandomNumberGenerator.Fill(output.AsSpan(12, 16)); RandomNumberGenerator.Fill(output.AsSpan(28, 12));
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(40, 4), plain.Length);
            passwordBytes = Encoding.UTF8.GetBytes(password);
            key = Rfc2898DeriveBytes.Pbkdf2(passwordBytes, output.AsSpan(12, 16), Iterations, HashAlgorithmName.SHA256, 32);
            token.ThrowIfCancellationRequested();
            using var cipher = new AesGcm(key, TagBytes);
            cipher.Encrypt(output.AsSpan(28, 12), plain, output.AsSpan(PayloadOffset), output.AsSpan(HeaderBytes, TagBytes), output.AsSpan(0, HeaderBytes));
            token.ThrowIfCancellationRequested(); return output;
        }
        finally { Clear(plain); Clear(passwordBytes); Clear(key); }
    }

    /// <summary>验证固定头和有界长度后才派生密钥；认证及 JSON 错误不暴露明文或解析详情。</summary>
    public static MountConfigurationBackup Decode(byte[] file, string password, CancellationToken token = default)
    {
        ValidatePassword(password); token.ThrowIfCancellationRequested();
        if (file.Length < PayloadOffset || file.Length > MaximumFileBytes || !file.AsSpan(0, 8).SequenceEqual("HMCONFIG"u8))
            throw new InvalidDataException("不是有效的 HybridMount 配置备份，或文件大小超限。");
        var format=BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(8,4));
        if (format is not(1 or 2 or 3 or 4 or 5)) throw new InvalidDataException("不支持此配置备份版本。");
        if (BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(40, 4)) != file.Length - PayloadOffset)
            throw new InvalidDataException("配置备份长度无效。");
        byte[]? plain = null, passwordBytes = null, key = null;
        try
        {
            plain = new byte[file.Length - PayloadOffset]; passwordBytes = Encoding.UTF8.GetBytes(password);
            key = Rfc2898DeriveBytes.Pbkdf2(passwordBytes, file.AsSpan(12, 16), Iterations, HashAlgorithmName.SHA256, 32);
            token.ThrowIfCancellationRequested();
            using var cipher = new AesGcm(key, TagBytes);
            cipher.Decrypt(file.AsSpan(28, 12), file.AsSpan(PayloadOffset), file.AsSpan(HeaderBytes, TagBytes), plain, file.AsSpan(0, HeaderBytes));
            var document = JsonSerializer.Deserialize<MountConfigurationBackup>(plain, Json) ?? throw new JsonException();
            if(document.SchemaVersion!=format)throw new JsonException();
            ValidateDocument(document); token.ThrowIfCancellationRequested(); return document;
        }
        catch (CryptographicException) { throw new InvalidDataException("密码不正确或文件已损坏。"); }
        catch (JsonException) { throw new InvalidDataException("密码不正确或文件已损坏。"); }
        finally { Clear(plain); Clear(passwordBytes); Clear(key); }
    }

    /// <summary>稳定读取有界文件；文件正在写入时拒绝共享，读完再次检查完整长度。</summary>
    public static async Task<MountConfigurationBackup> ReadAsync(string path, string password, CancellationToken token = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.Length < PayloadOffset || stream.Length > MaximumFileBytes) throw new InvalidDataException("配置备份大小无效或超过 8 MiB。");
        var bytes = new byte[(int)stream.Length]; await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (stream.Length != bytes.Length) throw new InvalidDataException("配置备份读取期间发生变化。");
        return Decode(bytes, password, token);
    }

    /// <summary>只落盘密文；同目录临时文件写完后原子替换，失败或取消保留原文件。</summary>
    /// <param name="commit">宿主可在停止锁内提交最终重命名；默认直接替换。</param>
    internal static async Task WriteAtomicAsync(string path, byte[] encrypted, CancellationToken token, Action<string, string>? commit = null)
    {
        token.ThrowIfCancellationRequested();
        var destination = Path.GetFullPath(path); var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(encrypted, token).ConfigureAwait(false); await stream.FlushAsync(token).ConfigureAwait(false); stream.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            if (commit is null) File.Move(temporary, destination, true); else commit(temporary, destination);
        }
        finally { File.Delete(temporary); }
    }

    /// <summary>验证安全的结构边界；路径和已实现协议的校验留给逐项导入预览。</summary>
    private static void ValidateDocument(MountConfigurationBackup backup)
    {
        if (backup.SchemaVersion is not(1 or 2 or 3 or 4 or 5) || backup.Mounts is null || backup.Mounts.Count is < 1 or > MaximumItems)
            throw new InvalidDataException("配置备份载荷版本或条目数量无效。");
        foreach (var item in backup.Mounts)
        {
            if(item is not null&&(item.RemoteIncrementalSeconds is <10 or >86400||item.RemoteFullSeconds is <30 or >604800))throw new InvalidDataException("配置备份包含无效检测间隔。");
            if (item is null || item.Name is null || item.LocalPath is null || item.Source is not {} source || item.Credentials is not {} secrets ||
                source.Endpoint is null || source.Bucket is null || source.Prefix is null || source.Region is null ||
                secrets.UserName is null || secrets.Password is null || secrets.AccessKey is null || secrets.SecretKey is null || secrets.SessionToken is null)
                throw new InvalidDataException("配置备份包含不完整条目。");
            try{CapacityRules.Validate(source.Kind,item.CapacityAutoDetectEnabled,item.MaximumCapacityBytes);FileExclusionRules.Validate(item.ExclusionRules);}
            catch(ArgumentException){throw new InvalidDataException("配置备份包含无效容量设置。");}
            if(backup.SchemaVersion<4&&(item.CapacityAutoDetectEnabled||item.MaximumCapacityBytes is not null))throw new InvalidDataException("旧版备份不能包含容量设置。");
            if(backup.SchemaVersion<5&&item.ExclusionRules is {Count:>0})throw new InvalidDataException("旧版备份不能包含排除规则。");
            FileSourceFactory.ValidateBackupFields(item);
        }
    }
    // v1 生成只供兼容回归使用，不能在 v1 容器中悄悄写入新版选项。
    private sealed record VersionFourItem(string Name,string LocalPath,FileSourceConfiguration Source,SourceCredentials Credentials,bool AutoConnect,bool ThumbnailsEnabled,bool RemoteAutoDetectEnabled,int RemoteIncrementalSeconds,int RemoteFullSeconds,SqsConfiguration? Sqs,SourceCredentials? SqsCredentials,bool CapacityAutoDetectEnabled,long? MaximumCapacityBytes);
    private sealed record VersionFourBackup(int SchemaVersion,DateTimeOffset ExportedUtc,IReadOnlyList<VersionFourItem> Mounts);
    private sealed record LegacyItem(string Name,string LocalPath,FileSourceConfiguration Source,SourceCredentials Credentials,bool AutoConnect,bool ThumbnailsEnabled);
    private sealed record VersionThreeItem(string Name,string LocalPath,FileSourceConfiguration Source,SourceCredentials Credentials,bool AutoConnect,bool ThumbnailsEnabled,bool RemoteAutoDetectEnabled,int RemoteIncrementalSeconds,int RemoteFullSeconds,SqsConfiguration? Sqs,SourceCredentials? SqsCredentials);
    private sealed record VersionThreeBackup(int SchemaVersion,DateTimeOffset ExportedUtc,IReadOnlyList<VersionThreeItem> Mounts);
    private sealed record VersionTwoItem(string Name,string LocalPath,FileSourceConfiguration Source,SourceCredentials Credentials,bool AutoConnect,bool ThumbnailsEnabled,bool RemoteAutoDetectEnabled,int RemoteIncrementalSeconds,int RemoteFullSeconds);
    private sealed record VersionTwoBackup(int SchemaVersion,DateTimeOffset ExportedUtc,IReadOnlyList<VersionTwoItem> Mounts);
    private sealed record LegacyBackup(int SchemaVersion,DateTimeOffset ExportedUtc,IReadOnlyList<LegacyItem> Mounts);
    private static void ValidatePassword(string password)
    { if (password is null || password.Length < 8) throw new ArgumentException("备份密码至少需要 8 个字符。"); }
    private static void Clear(byte[]? bytes) { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
}
