using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HybridMount.Models.Mounting;
namespace HybridMount.ViewModels;

/// <summary>规则编辑行；只保存页面草稿，控件变化不直接写配置或触发扫描。</summary>
public sealed class FileExclusionRuleDraft : ObservableObject
{
    private int _matchIndex;private string _text;
    public FileExclusionRuleDraft(FileExclusionRule rule){_matchIndex=(int)rule.Match;_text=rule.Text;}
    public int MatchIndex{get=>_matchIndex;set{if(value is >=0 and <=2)SetProperty(ref _matchIndex,value);}}
    public string Text{get=>_text;set=>SetProperty(ref _text,value);}
    public FileExclusionRule ToRule()=>new((FileExclusionMatchKind)_matchIndex,_text);
}

public sealed partial class MountsViewModel
{
    private readonly Dictionary<(Guid Id,FileSourceKind Kind),ObservableCollection<FileExclusionRuleDraft>> _exclusionDrafts=[];
    /// <summary>旧挂载缺失字段为空；新增草稿按协议独立保留，默认规则在第一次连接前即可编辑。</summary>
    public ObservableCollection<FileExclusionRuleDraft> ExclusionDraft(Guid? id,FileSourceKind kind)
    {
        var key=(id??Guid.Empty,kind);
        if(_exclusionDrafts.TryGetValue(key,out var draft))return draft;
        var rules=id is null?FileExclusionRules.Defaults():FindMount(id.Value)?.Definition.ExclusionRules??[];
        return _exclusionDrafts[key]=new(rules.Select(x=>new FileExclusionRuleDraft(x)));
    }
    /// <summary>返回完整校验后的快照；不修剪字面文本，不因读取草稿启用旧挂载规则。</summary>
    public IReadOnlyList<FileExclusionRule> ReadExclusionRules(Guid? id,FileSourceKind kind)=>FileExclusionRules.Validate(ExclusionDraft(id,kind).Select(x=>x.ToRule()));
    public void AddExclusionRule(Guid? id,FileSourceKind kind){if(!IsStopping)ExclusionDraft(id,kind).Add(new(new(FileExclusionMatchKind.Contains,"")));}
    public void RemoveExclusionRule(Guid? id,FileSourceKind kind,FileExclusionRuleDraft row){if(!IsStopping)ExclusionDraft(id,kind).Remove(row);}
    /// <summary>只补缺失的默认项；自定义规则不被覆盖。</summary>
    public void AddDefaultExclusionRules(Guid? id,FileSourceKind kind)
    {
        if(IsStopping)return;var draft=ExclusionDraft(id,kind);
        foreach(var rule in FileExclusionRules.Defaults())
            if(!draft.Any(x=>x.MatchIndex==(int)rule.Match&&x.Text.Equals(rule.Text,StringComparison.OrdinalIgnoreCase)))draft.Add(new(rule));
    }
    /// <summary>按挂载防重入；失败保留旧定义和草稿，成功后刷新状态但不重连。</summary>
    public Task SaveExclusionsAsync(Guid id)=>RunForMountAsync(id,async mount=>
    {
        var rules=ReadExclusionRules(id,mount.Definition.Source!.Kind);await _manager.SetFileExclusionRulesAsync(id,rules);
        if(!IsStopping)Status=global::HybridMount.Services.Localization.UiText.Text("Text_70E7E5F6BC2F",_manager.FileExclusionStatus(id));
    });
    /// <summary>设置窗口的简明容量来自已保存配置及同代缓存，草稿不会冒充已生效的上限。</summary>
    public static string CapacitySummary(MountSnapshot mount)
    {
        static string Gb(long? value)=>value is {} bytes?((decimal)bytes/1_000_000_000m).ToString("0.0",CultureInfo.CurrentCulture)+"GB":"—";
        var automatic=mount.Definition.CapacityAutoDetectEnabled;var value=mount.Capacity?.Value;
        var total=automatic?Gb(value?.TotalBytes):mount.Definition.MaximumCapacityBytes is {} maximum?Gb(maximum):global::HybridMount.Services.Localization.UiText.Text("Text_F203D577D142");
        var available=!automatic&&mount.Definition.MaximumCapacityBytes is null?"—":Gb(value?.AvailableBytes);
        return global::HybridMount.Services.Localization.UiText.Text("Text_1A9F134D31F9",available,total,(automatic?global::HybridMount.Services.Localization.UiText.Text("Text_3189296AE210"):global::HybridMount.Services.Localization.UiText.Text("Text_718CA0358795")),(mount.Capacity?.IsRefreshing==true?global::HybridMount.Services.Localization.UiText.Text("Text_67D79EEC4C8E"):""),(mount.Capacity?.Error is {} error?"\n"+error:""));
    }
    /// <summary>RFC6578标题已有模式信息；保留其他实际模式、最近成功与错误，不能隐藏降级。</summary>
    public static string DetectionSummary(MountSnapshot mount)
    {
        if(!mount.Definition.RemoteAutoDetectEnabled)return global::HybridMount.Services.Localization.UiText.Text("Text_6744B4C6A9AA");
        var status=mount.Detection;var mode=status?.Mode??global::HybridMount.Services.Localization.UiText.Text("Text_4D2966C43800");
        return global::HybridMount.Services.Localization.UiText.Text("Text_D076BD51ADFE",(mount.Definition.Source?.Kind==FileSourceKind.WebDav&&mode=="RFC 6578 增量检测"?"":mode+"；"),(status?.LastSuccessUtc?.ToLocalTime().ToString("g")??global::HybridMount.Services.Localization.UiText.Text("Text_0BA9585C89B8")),(status?.Error is {} error?"；"+error:""));
    }
}
