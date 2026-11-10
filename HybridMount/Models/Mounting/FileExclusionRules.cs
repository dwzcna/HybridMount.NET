namespace HybridMount.Models.Mounting;

/// <summary>文件名的字面匹配方式；不解析通配符、正则或目录路径。</summary>
public enum FileExclusionMatchKind { Contains, Prefix, Suffix }

/// <summary>每挂载的可选上传排除规则；文本保留原样，匹配忽略大小写。</summary>
public sealed record FileExclusionRule(FileExclusionMatchKind Match, string Text, bool Enabled = true);

/// <summary>上传排除的纯规则；不访问文件、配置或网络，也不把目录命中传播给后代。</summary>
public static class FileExclusionRules
{
    /// <summary>仅由普通新增挂载显式选用；旧定义和旧备份缺失字段始终表示空规则。</summary>
    public static FileExclusionRule[] Defaults() =>
        [new(FileExclusionMatchKind.Contains, "~"), new(FileExclusionMatchKind.Contains, "$"),
         new(FileExclusionMatchKind.Suffix, ".bak"), new(FileExclusionMatchKind.Prefix, "-")];

    /// <summary>复制并校验完整列表；禁止空值、路径和语义相同的重复项，失败不返回部分配置。</summary>
    public static FileExclusionRule[] Validate(IEnumerable<FileExclusionRule>? rules)
    {
        var result = rules?.ToArray() ?? [];
        if (result.Length > 256) throw new ArgumentException("排除规则最多 256 条。");
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in result)
        {
            if (rule is null || !Enum.IsDefined(rule.Match) || string.IsNullOrWhiteSpace(rule.Text) || rule.Text.Length > 255 ||
                rule.Text.Any(c => c is '/' or '\\' or '\0' || char.IsControl(c)))
                throw new ArgumentException("排除规则须填写有效文件名文本，不能包含路径分隔符或控制字符。");
            if (!keys.Add($"{rule.Match}:{rule.Text}")) throw new ArgumentException("排除规则重复，请合并相同匹配方式和文本。");
        }
        return result;
    }

    /// <summary>任一启用项命中文件名即排除；目录及空名称永不命中。</summary>
    public static bool Matches(IReadOnlyList<FileExclusionRule>? rules, string path, bool isDirectory = false)
    {
        if (isDirectory || rules is null || rules.Count == 0) return false;
        var name = path[(Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\')) + 1)..];
        return name.Length != 0 && rules.Any(rule => rule.Enabled && rule.Match switch
        {
            FileExclusionMatchKind.Contains => name.Contains(rule.Text, StringComparison.OrdinalIgnoreCase),
            FileExclusionMatchKind.Prefix => name.StartsWith(rule.Text, StringComparison.OrdinalIgnoreCase),
            FileExclusionMatchKind.Suffix => name.EndsWith(rule.Text, StringComparison.OrdinalIgnoreCase),
            _ => false
        });
    }

    /// <summary>副本也检查原文件及目标文件名，不能通过另存绕过排除。</summary>
    public static bool Matches(MountDefinition definition, LocalUpload item) =>
        Matches(definition.ExclusionRules, item.LocalPath, item.IsDirectory) ||
        item.IsCopy && Matches(definition.ExclusionRules, item.SourcePath, item.IsDirectory);

    /// <summary>仅暂停未发送的自动工作，保留错误、快照、原始阶段及问题；在途、失败和取消状态保持。</summary>
    public static LocalUpload Pause(LocalUpload item) => item.State is "Queued" or "Waiting" or "Prepared" or "WaitingCapacity" or "AwaitingConfirmation"
        ? item with { State = "Excluded", ExclusionResumeState = item.State, SnapshotAttemptId = null } : item;

    /// <summary>解除规则只恢复被排除暂停的阶段，不授权重试失败、冲突或主动取消。</summary>
    public static LocalUpload Resume(LocalUpload item) => item.State == "Excluded"
        ? item with { State = item.ExclusionResumeState is "Queued" or "Waiting" or "Prepared" or "WaitingCapacity" or "AwaitingConfirmation" ? item.ExclusionResumeState : "Queued", ExclusionResumeState = null }
        : item;
}
