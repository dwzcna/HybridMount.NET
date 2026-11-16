namespace HybridMount.Infrastructure.Settings;

/// <summary>固定白名单语言及有界离线政策，只读随包文件，不访问网络或用户挂载。</summary>
internal static class PrivacyPolicyReader
{
    internal static async Task<string> ReadAsync(string language, CancellationToken token)
    {
        var selected = language == "en-US" ? "English" : language == "zh-HK" ? "ChineseTraditional" : "ChineseSimplified";
        var path = Path.Combine(AppContext.BaseDirectory, "Privacy", selected + ".md");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        if (stream.Length > 256 * 1024) throw new IOException("Privacy policy exceeds the supported size.");
        using var reader = new StreamReader(stream, new System.Text.UTF8Encoding(false, true));
        return await reader.ReadToEndAsync(token).ConfigureAwait(false);
    }
}
