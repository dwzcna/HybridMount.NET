using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using HybridMount.Models.Mounting;

namespace HybridMount.Models.Persistence;

/// <summary>.hmxbk v1 的便携 JSON 编解码；后台执行，只加密凭据，不联网或依赖本机密钥。</summary>
/// <remarks>非敏感选项允许编辑；GCM 认证绑定版本、备份条目标识及凭据用途，不认证服务器地址。</remarks>
public static class MountBackupCodec
{
    public const int CurrentVersion = 1;
    public const int MaximumFileBytes = 8 * 1024 * 1024;
    public const int MaximumItems = 1000;
    private const int Iterations = 600_000, TagBytes = 16;
    private const string InvalidFile = "配置备份格式无效或文件已损坏。";
    private const string AuthenticationFailed = "密码不正确或文件已损坏。";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 16, WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    /// <summary>序列化用户选项；空密码产生明文 JSON，否则随机盐派生密钥并逐项保护凭据。取消不返回结果。</summary>
    public static byte[] Encode(MountConfigurationBackup backup, string password, CancellationToken token = default)
    {
        ValidatePassword(password); ValidateDocument(backup); token.ThrowIfCancellationRequested();
        byte[]? key = null;
        try
        {
            var salt = password.Length == 0 ? null : RandomNumberGenerator.GetBytes(16);
            var protection = salt is null ? new Protection("none") : new Protection("password", "PBKDF2-SHA256", Iterations, Convert.ToBase64String(salt), "AES-256-GCM");
            if (salt is not null) key = DeriveKey(password, salt);
            token.ThrowIfCancellationRequested();
            var entries = new List<BackupEntry>(); var nonces = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in backup.Mounts)
            {
                token.ThrowIfCancellationRequested(); var id = Guid.NewGuid(); EncryptedCredentials? encrypted = null;
                if (key is not null)
                {
                    var plain = JsonSerializer.SerializeToUtf8Bytes(new CredentialPayload(item.Credentials, item.SqsCredentials), Json);
                    try
                    {
                        byte[] nonce; string nonceText;
                        do { nonce = RandomNumberGenerator.GetBytes(12); nonceText = Convert.ToBase64String(nonce); } while (!nonces.Add(nonceText));
                        var ciphertext = new byte[plain.Length]; var tag = new byte[TagBytes];
                        using var cipher = new AesGcm(key, TagBytes);
                        cipher.Encrypt(nonce, plain, ciphertext, tag, AuthenticationData(id));
                        encrypted = new(nonceText, Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag));
                    }
                    finally { Clear(plain); }
                }
                entries.Add(new(id, item.Name, item.LocalPath, item.Source, item.AutoConnect, item.ThumbnailsEnabled,
                    item.RemoteAutoDetectEnabled, item.RemoteIncrementalSeconds, item.RemoteFullSeconds, item.Sqs,
                    item.CapacityAutoDetectEnabled, item.MaximumCapacityBytes, item.ExclusionRules,
                    key is null ? item.Credentials : null, key is null ? item.SqsCredentials : null, encrypted));
            }
            var output = JsonSerializer.SerializeToUtf8Bytes(new BackupFile("hmxbk", CurrentVersion, backup.ExportedUtc, protection, entries), Json);
            if (output.Length > MaximumFileBytes) { Clear(output); throw new InvalidDataException("配置备份超过 8 MiB 限制。"); }
            if (token.IsCancellationRequested) { Clear(output); token.ThrowIfCancellationRequested(); }
            return output;
        }
        finally { Clear(key); }
    }

    /// <summary>验证完整结构后解密凭据；错误密码和认证失败统一返回安全错误，不暴露正文。调用者须在后台执行。</summary>
    public static MountConfigurationBackup Decode(byte[] file, string password, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); var document = Parse(file); byte[]? key = null;
        try
        {
            if (document.Protection.Mode == "password")
            {
                if (string.IsNullOrEmpty(password)) throw new InvalidDataException("此配置备份需要密码。");
                ValidatePassword(password); key = DeriveKey(password, Base64(document.Protection.Salt!, 16));
            }
            token.ThrowIfCancellationRequested(); var items = new List<MountBackupItem>();
            foreach (var entry in document.Mounts)
            {
                token.ThrowIfCancellationRequested(); var credentials = entry.Credentials; var additional = entry.SqsCredentials;
                if (key is not null)
                {
                    var encrypted = entry.ProtectedCredentials!; var ciphertext = Base64(encrypted.Ciphertext); var plain = new byte[ciphertext.Length];
                    try
                    {
                        using var cipher = new AesGcm(key, TagBytes);
                        cipher.Decrypt(Base64(encrypted.Nonce, 12), ciphertext, Base64(encrypted.Tag, TagBytes), plain, AuthenticationData(entry.BackupEntryId));
                        using var json = StrictDocument(plain);
                        var payload = json.RootElement.Deserialize<CredentialPayload>(Json) ?? throw new JsonException();
                        credentials = payload.Credentials; additional = payload.SqsCredentials;
                    }
                    finally { Clear(plain); }
                }
                items.Add(ToItem(entry, credentials!, additional));
            }
            var result = new MountConfigurationBackup(CurrentVersion, document.ExportedUtc, items);
            ValidateDocument(result); token.ThrowIfCancellationRequested(); return result;
        }
        catch (CryptographicException) { throw new InvalidDataException(AuthenticationFailed); }
        catch (JsonException) { throw new InvalidDataException(AuthenticationFailed); }
        finally { Clear(key); }
    }

    /// <summary>从同一稳定文件读取有界快照；识别与后续密码重试都使用此快照，调用方负责释放会话。</summary>
    internal static async Task<MountBackupReadSession> OpenAsync(string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); ValidatePath(path); byte[]? bytes = null;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            if (stream.Length is < 2 or > MaximumFileBytes) throw new InvalidDataException("配置备份大小无效或超过 8 MiB。");
            bytes = new byte[(int)stream.Length]; await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            if (stream.Length != bytes.Length) throw new InvalidDataException("配置备份读取期间发生变化。");
            var requiresPassword = Parse(bytes).Protection.Mode == "password"; token.ThrowIfCancellationRequested();
            var session = new MountBackupReadSession(bytes, requiresPassword); bytes = null; return session;
        }
        finally { Clear(bytes); }
    }

    /// <summary>后台一次性读取接口；使用可释放会话，不保留文件句柄或密码。</summary>
    public static async Task<MountConfigurationBackup> ReadAsync(string path, string password, CancellationToken token = default)
    {
        using var session = await OpenAsync(path, token).ConfigureAwait(false);
        return session.Decode(password, token);
    }

    /// <summary>只写最终格式的字节；同目录临时文件完成后原子替换，取消或失败保留原文件并清理临时文件。</summary>
    /// <param name="commit">宿主可在停止锁内提交最终重命名；默认直接替换。</param>
    internal static async Task WriteAtomicAsync(string path, byte[] content, CancellationToken token, Action<string, string>? commit = null)
    {
        token.ThrowIfCancellationRequested(); ValidatePath(path);
        var destination = Path.GetFullPath(path); var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(content, token).ConfigureAwait(false); await stream.FlushAsync(token).ConfigureAwait(false); stream.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            if (commit is null) File.Move(temporary, destination, true); else commit(temporary, destination);
        }
        finally { File.Delete(temporary); }
    }

    /// <summary>仅接受新扩展名；文件内容仍独立验证，旧容器改名也不能导入。</summary>
    internal static void ValidatePath(string path)
    { if (!string.Equals(Path.GetExtension(path), ".hmxbk", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("仅支持 .hmxbk 配置备份；旧格式已停止支持。"); }

    /// <summary>派生前验证算法、长度、每项身份及明密文互斥；所有解析错误转换为不含输入正文的错误。</summary>
    private static BackupFile Parse(byte[] file)
    {
        if (file.Length is < 2 or > MaximumFileBytes) throw new InvalidDataException("配置备份大小无效或超过 8 MiB。");
        if (file.AsSpan().StartsWith("HMCONFIG"u8)) throw new InvalidDataException("仅支持 .hmxbk 配置备份；旧格式已停止支持。");
        try
        {
            using var json = StrictDocument(file);
            var document = json.RootElement.Deserialize<BackupFile>(Json) ?? throw new JsonException();
            if (document.Format != "hmxbk" || document.SchemaVersion != CurrentVersion) throw new InvalidDataException("不支持此配置备份格式或版本。");
            if (document.ExportedUtc == default || document.Mounts is null || document.Mounts.Count is < 1 or > MaximumItems || document.Protection is not {} protection) throw new JsonException();
            if (protection.Mode == "none")
            {
                if (protection.Kdf is not null || protection.Iterations is not null || protection.Salt is not null || protection.Cipher is not null) throw new JsonException();
            }
            else if (protection.Mode == "password")
            {
                if (protection.Kdf != "PBKDF2-SHA256" || protection.Iterations != Iterations || protection.Cipher != "AES-256-GCM" || protection.Salt is null) throw new JsonException();
                Base64(protection.Salt, 16);
            }
            else throw new JsonException();
            var ids = new HashSet<Guid>(); var nonces = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in document.Mounts)
            {
                if (entry is null || entry.BackupEntryId == Guid.Empty || !ids.Add(entry.BackupEntryId)) throw new JsonException();
                if (protection.Mode == "none")
                { if (entry.Credentials is null || entry.ProtectedCredentials is not null) throw new JsonException(); }
                else
                {
                    if (entry.Credentials is not null || entry.SqsCredentials is not null || entry.ProtectedCredentials is not {} encrypted) throw new JsonException();
                    Base64(encrypted.Nonce, 12); Base64(encrypted.Tag, TagBytes); var payload = Base64(encrypted.Ciphertext);
                    if (payload.Length == 0 || !nonces.Add(encrypted.Nonce)) throw new JsonException();
                }
                ValidateItem(ToItem(entry, entry.Credentials ?? new(), entry.SqsCredentials));
            }
            return document;
        }
        catch (JsonException) { throw new InvalidDataException(InvalidFile); }
        catch (FormatException) { throw new InvalidDataException(InvalidFile); }
        catch (ArgumentException) { throw new InvalidDataException(InvalidFile); }
    }

    /// <summary>严格 JSON：最大深度 16，任何层级的重复字段均拒绝，避免解析器覆盖导致模式歧义。</summary>
    private static JsonDocument StrictDocument(byte[] bytes)
    {
        // 接受编辑器写入的 UTF-8 BOM，导出自身不写 BOM。
        var memory = bytes.AsMemory(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0);
        var document = JsonDocument.Parse(memory, new JsonDocumentOptions { MaxDepth = 16 });
        try { CheckDuplicates(document.RootElement); return document; } catch { document.Dispose(); throw; }
    }
    private static void CheckDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject()) { if (!names.Add(property.Name)) throw new JsonException(); CheckDuplicates(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) CheckDuplicates(item);
    }
    private static byte[] Base64(string value, int? length = null)
    {
        if (value is null) throw new JsonException();
        var bytes = Convert.FromBase64String(value);
        if ((length is not null && bytes.Length != length) || Convert.ToBase64String(bytes) != value) throw new JsonException();
        return bytes;
    }
    private static byte[] AuthenticationData(Guid id) => Encoding.UTF8.GetBytes($"hmxbk|1|{id:D}|credentials");
    private static byte[] DeriveKey(string password, byte[] salt)
    {
        var bytes = Encoding.UTF8.GetBytes(password);
        try { return Rfc2898DeriveBytes.Pbkdf2(bytes, salt, Iterations, HashAlgorithmName.SHA256, 32); }
        finally { Clear(bytes); }
    }
    private static MountBackupItem ToItem(BackupEntry entry, SourceCredentials credentials, SourceCredentials? additional)
        => new(entry.Name, entry.LocalPath, entry.Source, credentials, entry.AutoConnect, entry.ThumbnailsEnabled,
            entry.RemoteAutoDetectEnabled, entry.RemoteIncrementalSeconds, entry.RemoteFullSeconds, entry.Sqs, additional,
            entry.CapacityAutoDetectEnabled, entry.MaximumCapacityBytes, entry.ExclusionRules);
    private static void ValidateDocument(MountConfigurationBackup backup)
    {
        if (backup.SchemaVersion != CurrentVersion || backup.ExportedUtc == default || backup.Mounts is null || backup.Mounts.Count is < 1 or > MaximumItems)
            throw new InvalidDataException("配置备份载荷版本或条目数量无效。");
        foreach (var item in backup.Mounts) ValidateItem(item);
    }
    private static void ValidateItem(MountBackupItem item)
    {
        if (item is null || item.Name is null || item.LocalPath is null || item.Source is not {} source || item.Credentials is not {} secrets ||
            source.Kind is not (FileSourceKind.WebDav or FileSourceKind.S3 or FileSourceKind.Synology) ||
            source.Endpoint is null || source.Bucket is null || source.Prefix is null || source.Region is null ||
            secrets.UserName is null || secrets.Password is null || secrets.AccessKey is null || secrets.SecretKey is null || secrets.SessionToken is null ||
            item.SqsCredentials is {} extra && (extra.UserName is null || extra.Password is null || extra.AccessKey is null || extra.SecretKey is null || extra.SessionToken is null))
            throw new InvalidDataException("配置备份包含不完整条目。");
        if (item.RemoteIncrementalSeconds is < 10 or > 86400 || item.RemoteFullSeconds is < 30 or > 604800) throw new InvalidDataException("配置备份包含无效检测间隔。");
        try { CapacityRules.Validate(source.Kind, item.CapacityAutoDetectEnabled, item.MaximumCapacityBytes); FileExclusionRules.Validate(item.ExclusionRules); FileSourceFactory.ValidateBackupFields(item); }
        catch (Exception error) when (error is ArgumentException or InvalidDataException or NotSupportedException) { throw new InvalidDataException("配置备份包含无效连接参数或选项。"); }
    }
    private static void ValidatePassword(string password)
    { if (password is null || password.Length is > 0 and < 8) throw new ArgumentException("启用密码保护时至少需要 8 个字符。"); }
    private static void Clear(byte[]? bytes) { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }

    // 仅属于外部备份的白名单；不复用本机旧字段迁移转换器，所有未知字段由 Json 严格拒绝。
    private sealed record Protection(string Mode, string? Kdf = null, int? Iterations = null, string? Salt = null, string? Cipher = null);
    private sealed record EncryptedCredentials(string Nonce, string Ciphertext, string Tag);
    private sealed record CredentialPayload([property: JsonRequired] SourceCredentials Credentials, SourceCredentials? SqsCredentials);
    private sealed record BackupFile(string Format, int SchemaVersion, DateTimeOffset ExportedUtc, Protection Protection, IReadOnlyList<BackupEntry> Mounts);
    private sealed record BackupEntry([property: JsonRequired] Guid BackupEntryId, [property: JsonRequired] string Name,
        [property: JsonRequired] string LocalPath, [property: JsonRequired] FileSourceConfiguration Source,
        [property: JsonRequired] bool AutoConnect, [property: JsonRequired] bool ThumbnailsEnabled, [property: JsonRequired] bool RemoteAutoDetectEnabled,
        [property: JsonRequired] int RemoteIncrementalSeconds, [property: JsonRequired] int RemoteFullSeconds, SqsConfiguration? Sqs,
        [property: JsonRequired] bool CapacityAutoDetectEnabled, long? MaximumCapacityBytes, IReadOnlyList<FileExclusionRule>? ExclusionRules,
        SourceCredentials? Credentials, SourceCredentials? SqsCredentials, EncryptedCredentials? ProtectedCredentials);
}
