namespace HybridMount.Models.Mounting.WebDAV;

/// <summary>来自已有 OPTIONS 的连接级诊断证据；通告不等于经过验证的写入能力。</summary>
internal sealed record WebDavCapabilitySnapshot(DateTimeOffset ObservedUtc,string Allow,string PatchFormats,bool SabrePartialUpdate)
{
    public bool PatchAdvertised=>Allow.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).Contains("PATCH",StringComparer.OrdinalIgnoreCase);
    public bool HasPatchFormats=>PatchFormats.Length>0;
    public override string ToString()=>$"OPTIONS 采样：{ObservedUtc.ToLocalTime():O}\nAllow：{(Allow.Length==0?"未通告":Allow)}\nRFC 5789：PATCH 方法{(PatchAdvertised?"已通告":"未通告")}；补丁格式{(HasPatchFormats?"已声明":"未声明")}；实际写入未验证。\nAccept-Patch：{(HasPatchFormats?PatchFormats:"未声明")}\nsabredav-partialupdate：{(SabrePartialUpdate?"已通告":"未通告")}；上传继续使用条件 PUT。";
}
