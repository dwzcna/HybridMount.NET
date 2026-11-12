using System.Collections.Concurrent;
using HybridMount.Helpers.Threading;
namespace HybridMount.Tests;

/// <summary>只用可控屏障验证后台读取与Dispatcher发布；不运行WinUI或访问配置。</summary>
public sealed class BackgroundSnapshotPumpTests
{
    private sealed class Dispatcher:IUiDispatcher
    {
        internal readonly ConcurrentQueue<Action> Pending=new();
        public bool TryEnqueue(Action callback){Pending.Enqueue(callback);return true;}
        internal void Drain(){while(Pending.TryDequeue(out var action))action();}
    }
    [Fact(Timeout=15000)]
    public async Task ReadRunsInBackgroundAndObsoleteQueryNeverPublishes()
    {
        var ui=new Dispatcher();using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();var published=new List<string>();
        using var pump=new BackgroundSnapshotPump<string,string>(ui,query=>{if(query=="old"){entered.Set();release.Wait();}return query;},published.Add,error=>throw error);
        pump.Request("old");Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));pump.Request("new");Assert.Empty(published);release.Set();
        await pump.WhenIdle;Assert.Empty(published);ui.Drain();Assert.Equal(new[]{"new"},published);
    }
    [Fact(Timeout=15000)]
    public async Task RepeatedProgressCoalescesAndDisposalDropsQueuedPublication()
    {
        var ui=new Dispatcher();using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();var reads=0;var published=0;
        using var pump=new BackgroundSnapshotPump<int,int>(ui,_=>{if(Interlocked.Increment(ref reads)==1){entered.Set();release.Wait();}return reads;},_=>published++,error=>throw error);
        pump.Request(0);Assert.True(entered.Wait(TimeSpan.FromSeconds(2)));for(var i=0;i<1000;i++)pump.Request(0);release.Set();await pump.WhenIdle;
        Assert.Equal(2,reads);Assert.Equal(0,published);pump.Dispose();ui.Drain();Assert.Equal(0,published);
    }
}
