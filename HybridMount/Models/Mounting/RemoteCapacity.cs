using System.Globalization;

namespace HybridMount.Models.Mounting;

/// <summary>服务器针对配置根报告的配额；空值表示未知，不代表零或服务器物理磁盘容量。</summary>
public sealed record RemoteCapacity(string Scope,long? AvailableBytes=null,long? UsedBytes=null,long? ConfiguredTotalBytes=null)
{
    /// <summary>手动上限独立保存；自动模式仅将同一响应中的有效已用与可用相加，溢出仍未知。</summary>
    public long? TotalBytes=>ConfiguredTotalBytes is >=0?ConfiguredTotalBytes:AvailableBytes is {} free&&UsedBytes is {} used&&free>=0&&used>=0&&free<=long.MaxValue-used?free+used:null;
    public static RemoteCapacity Unknown {get;}=new(global::HybridMount.Services.Localization.UILanguageService.Messages.GetString("UnknownNotSuppliedByTheService", global::HybridMount.Services.Localization.UILanguageService.ResourceCulture)!);
    /// <summary>RFC 4331 非负十进制整数；无限、负数、畸形与 Int64 溢出保持未知。</summary>
    public static long? ParseBytes(string? value)=>long.TryParse(value?.Trim(),NumberStyles.None,CultureInfo.InvariantCulture,out var bytes)&&bytes>=0?bytes:null;
}
/// <summary>连接内缓存的配额与成功时间；失败不覆盖旧值，查询状态不进入备份。</summary>
public sealed record RemoteCapacitySnapshot(RemoteCapacity Value,DateTimeOffset? LastSuccessUtc=null,
    DateTimeOffset? LastAttemptUtc=null,string? Error=null,bool IsRefreshing=false)
{
    private static string Bytes(long? value)=>value is {} bytes?TaskByteFormat.Format(bytes):global::HybridMount.Services.Localization.UILanguageService.Messages.GetString("Unknown", global::HybridMount.Services.Localization.UILanguageService.ResourceCulture)!;
    public string Display=>string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.Services.Localization.UILanguageService.Messages.GetString("RemoteCapacityAvailableUsedTotal", global::HybridMount.Services.Localization.UILanguageService.ResourceCulture)!, Value.Scope, Bytes(Value.AvailableBytes), Bytes(Value.UsedBytes), Bytes(Value.TotalBytes), (LastSuccessUtc is {} time?string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.Services.Localization.UILanguageService.Messages.GetString("LastSuccess", global::HybridMount.Services.Localization.UILanguageService.ResourceCulture)!, time.ToLocalTime()):""), (IsRefreshing?global::HybridMount.Services.Localization.UILanguageService.Messages.GetString("Querying", global::HybridMount.Services.Localization.UILanguageService.ResourceCulture)!:""), (Error is {} error?"\n"+error:""));
}
