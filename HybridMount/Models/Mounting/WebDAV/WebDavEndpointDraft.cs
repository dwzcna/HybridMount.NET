namespace HybridMount.Models.Mounting.WebDAV;

/// <summary>构造WebDAV编辑草稿，不访问系统或网络；允许半输入地址，由提交入口执行完整校验。</summary>
internal static class WebDavEndpointDraft
{
    /// <summary>新建和导入按显式URL设置HTTP意图；已有地址未变保留旧标记，避免无关编辑改变根身份。</summary>
    internal static FileSourceConfiguration Create(string endpoint, FileSourceConfiguration? existing, string? pin)
    {
        var unchanged = existing?.Kind == FileSourceKind.WebDav &&
            string.Equals(endpoint, existing.Endpoint.Trim(), StringComparison.Ordinal);
        var http = Uri.TryCreate(endpoint, UriKind.Absolute, out var address) && address.Scheme == "http";
        return new(FileSourceKind.WebDav, endpoint, AllowHttp: unchanged ? existing!.AllowHttp : http,
            PinnedCertificateSha256: string.IsNullOrWhiteSpace(pin) ? null : pin.Trim());
    }
}
