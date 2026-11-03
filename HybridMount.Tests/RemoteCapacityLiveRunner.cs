using System.Text.Json;
using HybridMount.Models.Mounting;
using HybridMount.Services;
using HybridMount.Services.Mounting;
using Microsoft.Win32;

namespace HybridMount.Tests;

/// <summary>显式只读配额验收；直接读取已有定义与 DPAPI，不迁移正式数据库、不注册同步根。</summary>
public static class RemoteCapacityLiveRunner
{
    /// <summary>仅访问指定已有 WebDAV 挂载，输出安全配额报告；凭据和服务原文不输出。</summary>
    public static async Task<string> RunOriginalAsync(string originalRoot,string outputDirectory,CancellationToken token)
    {
        MountDefinition? definition=null;
        using(var parent=Registry.CurrentUser.OpenSubKey(ApplicationPaths.RegistryPath+@"\Mounts\Unpackaged"))
        {
            foreach(var name in parent?.GetSubKeyNames()??[])
            {
                using var child=parent!.OpenSubKey(name);
                if(child?.GetValue("Definition") is not string json)continue;
                var candidate=JsonSerializer.Deserialize<MountDefinition>(json);
                if(candidate?.LocalPath.Equals(originalRoot,StringComparison.OrdinalIgnoreCase)==true)definition=candidate;
            }
        }
        if(definition?.Source?.Kind!=FileSourceKind.WebDav)throw new IOException("指定已有 WebDAV 挂载不存在。");
        Directory.CreateDirectory(outputDirectory);
        var credentials=new CredentialStore(Path.Combine(ApplicationPaths.DataDirectory,"Mounting")).Load(definition.Id);
        await using var source=new WebDavFileSource(definition.Source,credentials,new WholeFileCache(Path.Combine(outputDirectory,"cache")));
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var value=await source.GetRemoteCapacityAsync(deadline.Token).ConfigureAwait(false);
        var report=Path.Combine(outputDirectory,"report.json");
        await File.WriteAllTextAsync(report,JsonSerializer.Serialize(new{Version=typeof(RemoteCapacityLiveRunner).Assembly.GetName().Version?.ToString(),
            CheckedUtc=DateTimeOffset.UtcNow,MountId=definition.Id,OriginalRoot=originalRoot,Capacity=value,ReadOnly=true,
            RemoteWrites=0,SyncRootRegistrations=0,FormalDatabaseOpened=false},new JsonSerializerOptions{WriteIndented=true}),token).ConfigureAwait(false);
        return report;
    }
}
