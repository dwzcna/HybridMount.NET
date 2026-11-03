namespace HybridMount.Models.Mounting;

/// <summary>文件源与本地目录之间的受限映射，拒绝父级、设备名和无法无损表达的名称。</summary>
public static class RelativePathRules
{
    public static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        if (path.Contains('\\') || path.StartsWith('/') || path.EndsWith('/')) throw new ArgumentException("需要规范的相对路径。", nameof(path));
        foreach (var part in path.Split('/'))
        {
            if (part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("文件源名称无法安全映射到 Windows。", nameof(path));
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && "123456789¹²³".Contains(stem[3])))
                throw new ArgumentException("文件源包含 Windows 保留名称。", nameof(path));
        }
        return path;
    }
    public static string Parent(string path) { var split = path.LastIndexOf('/'); return split < 0 ? "" : path[..split]; }
    public static bool Contains(string parent, string path) => parent == "" || path.Equals(parent, StringComparison.OrdinalIgnoreCase) || path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);
    public static string ToLocal(string root, string relative)
    {
        relative = Normalize(relative);
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!full.Equals(prefix, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("路径越过挂载根。");
        return full;
    }
}
