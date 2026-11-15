using HybridMount.Models.Mounting;
using HybridMount.UI.Localization;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
namespace HybridMount.ViewModels;



public sealed partial class MountsViewModel
{
    private readonly Dictionary<(Guid Id, FileSourceKind Kind), ObservableCollection<FileExclusionRuleViewModel>> _exclusionDrafts = [];
    /// <summary>为导入表单创建独立规则集合；取消后可直接释放，不读写业务配置。</summary>
    internal Guid CreateImportExclusions(FileSourceKind kind, IReadOnlyList<FileExclusionRule>? rules)
    {
        var scope = Guid.NewGuid();
        _exclusionDrafts[(scope, kind)] = new((rules ?? []).Select(rule => new FileExclusionRuleViewModel(rule)));
        return scope;
    }
    internal void ReleaseImportExclusions(Guid scope, FileSourceKind kind) => _exclusionDrafts.Remove((scope, kind));
    /// <summary>旧挂载缺失字段为空；新增草稿按协议独立保留，默认规则在第一次连接前即可编辑。</summary>
    public ObservableCollection<FileExclusionRuleViewModel> ExclusionDraft(Guid? id, FileSourceKind kind)
    {
        var key = (id ?? Guid.Empty, kind);
        if (_exclusionDrafts.TryGetValue(key, out var draft)) return draft;
        var rules = id is null ? FileExclusionRules.Defaults() : FindMount(id.Value)?.Definition.ExclusionRules ?? [];
        return _exclusionDrafts[key] = new(rules.Select(x => new FileExclusionRuleViewModel(x)));
    }
    /// <summary>返回完整校验后的快照；不修剪字面文本，不因读取草稿启用旧挂载规则。</summary>
    public IReadOnlyList<FileExclusionRule> ReadExclusionRules(Guid? id, FileSourceKind kind) => FileExclusionRules.Validate(ExclusionDraft(id, kind).Select(x => x.ToRule()));
    public void AddExclusionRule(Guid? id, FileSourceKind kind) { if (!IsStopping) ExclusionDraft(id, kind).Add(new(new(FileExclusionMatchKind.Contains, ""))); }
    public void RemoveExclusionRule(Guid? id, FileSourceKind kind, FileExclusionRuleViewModel row) { if (!IsStopping) ExclusionDraft(id, kind).Remove(row); }
    /// <summary>只补缺失的默认项；自定义规则不被覆盖。</summary>
    public void AddDefaultExclusionRules(Guid? id, FileSourceKind kind)
    {
        if (IsStopping) return; var draft = ExclusionDraft(id, kind);
        foreach (var rule in FileExclusionRules.Defaults())
            if (!draft.Any(x => x.MatchIndex == (int)rule.Match && x.Text.Equals(rule.Text, StringComparison.OrdinalIgnoreCase))) draft.Add(new(rule));
    }
    /// <summary>按挂载防重入；失败保留旧定义和草稿，成功后刷新状态但不重连。</summary>
    public Task SaveExclusionsAsync(Guid id) => RunForMountAsync(id, async mount =>
    {
        var rules = ReadExclusionRules(id, mount.Definition.Source!.Kind); await _manager.SetFileExclusionRulesAsync(id, rules);
        if (!IsStopping) Status = string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("ExclusionRulesSaved", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, _manager.FileExclusionStatus(id));
    });
    /// <summary>设置窗口的简明容量来自已保存配置及同代缓存，草稿不会冒充已生效的上限。</summary>
    public static string CapacitySummary(MountSnapshot mount)
    {
        static string Gb(long? value) => value is { } bytes ? ((decimal)bytes / 1_000_000_000m).ToString("0.0", CultureInfo.CurrentCulture) + "GB" : "—";
        var automatic = mount.Definition.CapacityAutoDetectEnabled; var value = mount.Capacity?.Value;
        var total = automatic ? Gb(value?.TotalBytes) : mount.Definition.MaximumCapacityBytes is { } maximum ? Gb(maximum) : global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("Unlimited", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!;
        var available = !automatic && mount.Definition.MaximumCapacityBytes is null ? "—" : Gb(value?.AvailableBytes);
        return string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("AvailableTotalBasedOn", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, available, total, (automatic ? global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("ServerInformation", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)! : global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("UserInput", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!), (mount.Capacity?.IsRefreshing == true ? global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("FetchingCapacityInformation", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)! : ""), (mount.Capacity?.Error is { } error ? "\n" + error : ""));
    }
    /// <summary>RFC6578标题已有模式信息；保留其他实际模式、最近成功与错误，不能隐藏降级。</summary>
    public static string DetectionSummary(MountSnapshot mount)
    {
        if (!mount.Definition.RemoteAutoDetectEnabled) return global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("Off", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!;
        var status = mount.Detection; var mode = status?.Mode ?? global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("NotDetectedYet", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!;
        return string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("LastSuccessDetails", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, (mount.Definition.Source?.Kind == FileSourceKind.WebDav && mode == "RFC 6578 增量检测" ? "" : mode + "；"), (status?.LastSuccessUtc?.ToLocalTime().ToString("g") ?? global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("NoneYet", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!), (status?.Error is { } error ? "；" + error : ""));
    }
}
