using HybridMount.Models.Mounting;
using Windows.Storage;
using Windows.Storage.Provider;
namespace HybridMount.Services.Mounting;

/// <summary>通过系统存储提供程序属性发布状态图标；调用方持有属性保护句柄并负责身份日志、状态戳和关闭后的通知，不注册Shell扩展。</summary>
internal static class CloudItemStateProperty
{
    internal const int ExclusionId=2;
    internal const string ExclusionText="已排除（不上传）";
    internal static string ExclusionIcon=>ShellStatusIconResources.Excluded;
    /// <summary>只替换本程序拥有的编号，保留同根其他完整状态；来源不一致或向量损坏时拒绝覆盖。</summary>
    internal static (bool Changed,IReadOnlyList<ShellCustomProperty> Properties) Plan(MountDefinition mount,string path,int id,string text,string icon)
    {
        var stored=CloudCustomStateReader.Read(path);var change=stored.Replace(id,text,icon);
        if((change.Changed||text.Length>0)&&stored.Ids.Count>0&&stored.ProviderId!=CloudRootRegistration.Id(mount))
            throw new IOException("自定义状态来源不属于本同步根，未修改属性。");
        return change;
    }
    /// <summary>属性写入开始前响应取消；开始后等待系统实际完成再释放保护句柄，避免取消后的迟到写入。只读回编号、文字和图标，不读正文。</summary>
    internal static async Task<bool> SetAsync(MountDefinition mount,string path,bool directory,int id,string text,string icon,CancellationToken token)
    {
        var change=Plan(mount,path,id,text,icon);if(!change.Changed)return false;
        token.ThrowIfCancellationRequested();
        IStorageItem item=directory?await StorageFolder.GetFolderFromPathAsync(path).AsTask(token).ConfigureAwait(false):await StorageFile.GetFileFromPathAsync(path).AsTask(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        await StorageProviderItemProperties.SetAsync(item,change.Properties.Select(x=>new StorageProviderItemProperty{Id=x.Id,Value=x.Value,IconResource=x.Icon})).AsTask().ConfigureAwait(false);
        var actual=CloudCustomStateReader.Read(path);
        if(actual.ProviderId!=CloudRootRegistration.Id(mount)||actual.Ids.Count!=change.Properties.Count||actual.Values.Count!=actual.Ids.Count||actual.Icons.Count!=actual.Ids.Count||
            actual.Ids.Where((x,i)=>x!=change.Properties[i].Id.ToString(System.Globalization.CultureInfo.InvariantCulture)||actual.Values[i]!=change.Properties[i].Value||actual.Icons[i]!=change.Properties[i].Icon).Any())
            throw new IOException("自定义状态写后核对未通过，保留恢复记录。");
        return true;
    }
}
