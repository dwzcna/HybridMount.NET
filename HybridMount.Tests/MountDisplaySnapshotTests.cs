using System.Reflection;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>仓库完全阻塞时界面仍只读内存；后台收尾与代次屏障不依赖真实挂载或远端。</summary>
public sealed class MountDisplaySnapshotTests
{
    private sealed class Store(MountDefinition mount):IMountDefinitionStore
    {public IReadOnlyList<MountDefinition> LoadDefinitions()=>[mount];public void SaveDefinition(MountDefinition value)=>throw new NotSupportedException();public void DeleteDefinition(Guid id)=>throw new NotSupportedException();public long CacheLimitBytes{get;set;}}
    [Fact(Timeout=15000)]
    public async Task BusyRepositoryCannotBlockDisplayAndCountsPublishAfterItIsReleased()
    {
        var area=Path.Combine(Path.GetTempPath(),"HybridMount.Display61."+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(area);
        try
        {
            var mount=new MountDefinition(Guid.NewGuid(),"memory snapshot",Path.Combine(area,"view"),false,RegistrationStage:MountRegistrationStage.NotRegistered);
            var repository=new MountRepository(Path.Combine(area,"data"),definitions:new Store(mount));await using var logger=new LoggerService(Path.Combine(area,"log.db"));await using var manager=new MountManager(repository,logger);
            repository.SaveUpload(new("issue",mount.Id,"file.bin","file.bin",false,"stamp",1,"Failed",DateTimeOffset.UtcNow,Error:"failed"));
            var gate=typeof(MountRepository).GetField("_gate",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(repository)!;
            var held=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var busy=Task.Run(()=>{lock(gate){held.TrySetResult();release.Task.GetAwaiter().GetResult();}});
            await held.Task;
            try
            {
                manager.RefreshDisplaySnapshot();var snapshot=await Task.Run(()=>manager.DisplayMounts).WaitAsync(TimeSpan.FromSeconds(1));
                Assert.Equal(mount.Id,Assert.Single(snapshot).Definition.Id);Assert.Same(snapshot,manager.DisplayMounts);
                Assert.Equal(mount.Name,Assert.Single(manager.TasksQueue.Mounts).Definition.Name);
            }
            finally{release.TrySetResult();await busy;}
            var deadline=DateTime.UtcNow.AddSeconds(3);while(manager.DisplayMounts.Single().UploadIssueCount!=1&&DateTime.UtcNow<deadline)await Task.Delay(20);
            Assert.Equal(1,manager.DisplayMounts.Single().UploadIssueCount);await manager.DisposeAsync();var stopped=manager.DisplayMounts;
            manager.RefreshDisplaySnapshot();Assert.Same(stopped,manager.DisplayMounts);
        }
        finally{Directory.Delete(area,true);}
    }
}
