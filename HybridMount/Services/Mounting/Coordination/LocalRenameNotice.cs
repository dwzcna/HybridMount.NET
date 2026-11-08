using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

/// <summary>监控线程只复制改名前后路径；CFAPI补充已确认的原条目，实际身份读取和持久化在后台扫描执行。</summary>
internal sealed record LocalRenameNotice(string OldPath,string NewPath,MountEntry? Entry=null,bool? IntoExcluded=null);
