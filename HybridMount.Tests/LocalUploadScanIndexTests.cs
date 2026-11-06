using System.Collections;
using System.Reflection;
using HybridMount.Models.Mounting;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

public sealed class LocalUploadScanIndexTests
{
    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)][InlineData(6)][InlineData(7)]
    public void RandomizedIndexesExactlyMatchLinearRules(int seed)
    {
        var random=new Random(seed);string[] paths=["","a","A","a/b","a/b/c","a/bb","a/","/a","a//b","中文/文件","中文/文件/child","z","Z/file","İ","i","x-y"];
        var entries=Enumerable.Range(0,400).Select(_=>
        {
            var path=paths[random.Next(paths.Length)];return new MountEntry(random.Next(40).ToString(),random.Next(2)==0?path:paths[random.Next(paths.Length)],path,"version",random.Next(2)==0,
                Deleted:random.Next(2)==0,Materialized:random.Next(2)==0,RemotePresence:(RemotePresence)random.Next(3));
        }).ToArray();
        var index=new LocalUploadScanIndex(entries);
        foreach(var path in paths.Concat(paths.Select(path=>path+"/child")))
        {
            Assert.Same(entries.FirstOrDefault(entry=>!entry.Deleted&&entry.LocalPath.Equals(path,StringComparison.OrdinalIgnoreCase)),index.ActivePath(path));
            Assert.Same(UploadRules.DeletedSourceAncestor(entries,path),index.DeletedAncestor(path));
            Assert.Same(UploadRules.LocalReplacementBaseline(entries,path),index.ReplacementBaseline(path));
            foreach(var id in Enumerable.Range(0,42).Select(value=>value.ToString()))
            {
                Assert.Same(entries.FirstOrDefault(entry=>entry.Id==id),index.ById(id));
                Assert.Same(UploadRules.LocalReplacementBaseline(entries,path,id),index.ReplacementBaseline(path,id));
            }
        }
    }
    [Fact]
    public void EmptyIndexHasNoMatches()
    {
        var index=new LocalUploadScanIndex([]);Assert.Null(index.ById("missing"));Assert.Null(index.ActivePath(""));Assert.Null(index.ReplacementBaseline(""));Assert.Null(index.DeletedAncestor(""));Assert.Null(index.DeletedAncestor("a/b"));
    }
    [Fact]
    public void FirstMatchIsInputOrderNotNearestAncestorOrExactCase()
    {
        var deep=new MountEntry("deep","a/b","a/b","v",true,RemotePresence:RemotePresence.Deleted);
        var shallow=new MountEntry("shallow","a","a","v",true,RemotePresence:RemotePresence.Deleted);
        var upper=new MountEntry("duplicate","UPPER","UPPER","v",false);var lower=upper with{LocalPath="upper",Version="other"};
        var index=new LocalUploadScanIndex([deep,shallow,upper,lower]);Assert.Same(deep,index.DeletedAncestor("a/b/c"));Assert.Same(shallow,index.DeletedAncestor("a/bb"));Assert.Null(index.DeletedAncestor("ab"));Assert.Null(index.DeletedAncestor("A/b/c"));Assert.Same(upper,index.ActivePath("upper"));Assert.Same(upper,index.ById("duplicate"));
    }
    [Fact]
    public void HistoricalAmbiguityAndExactPathBindingArePreserved()
    {
        var original=new MountEntry("original","file","file","v",false,Deleted:true,Materialized:true);
        var second=original with{Id="second"};var index=new LocalUploadScanIndex([original,second]);Assert.Null(index.ReplacementBaseline("file"));Assert.Same(original,index.ReplacementBaseline("file","original"));Assert.Same(second,index.ReplacementBaseline("file","second"));Assert.Null(index.ReplacementBaseline("FILE","original"));Assert.Null(index.ReplacementBaseline("file","unknown"));
        Assert.Null(new LocalUploadScanIndex([original,original]).ReplacementBaseline("file"));Assert.Same(original,new LocalUploadScanIndex([original,original]).ReplacementBaseline("file","original"));
        foreach(var invalid in new[]{original with{Materialized=false},original with{SourcePath="renamed"},original with{RemotePresence=RemotePresence.Deleted},original with{Deleted=false}})
            Assert.Null(new LocalUploadScanIndex([invalid]).ReplacementBaseline("file","original"));
    }
    [Fact]
    public void TwentyThousandEntriesAreEnumeratedOnceAndQueriesArePathBounded()
    {
        var source=new CountedEntries(20000);var index=new LocalUploadScanIndex(source);Assert.Equal(20000,source.Visits);Assert.Equal(1,source.Enumerations);long steps=0,bound=0;
        for(var number=0;number<20000;number++)
        {
            var path=$"folder/{number:D5}";Assert.NotNull(index.ById(number.ToString()));Assert.NotNull(index.ActivePath(path.ToUpperInvariant()));Assert.Null(index.ReplacementBaseline(path));
            var child=path+"/child";Assert.Equal(number.ToString(),index.DeletedAncestor(child,out var count)!.Id);steps+=count;bound+=child.Length;
        }
        Assert.InRange(steps,1,bound);Assert.Equal(20000,source.Visits);Assert.Equal(1,source.Enumerations);
    }
    private sealed class CountedEntries(int count):IEnumerable<MountEntry>
    {
        internal int Visits,Enumerations;
        public IEnumerator<MountEntry> GetEnumerator(){Enumerations++;for(var index=0;index<count;index++){Visits++;yield return new(index.ToString(),$"folder/{index:D5}",$"folder/{index:D5}","v",false,RemotePresence:RemotePresence.Deleted);}}
        IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
    }
    [Fact]
    public void SchedulerWakesDoNotRequestScanButEveryWatcherEventDoes()
    {
        using var runtime=new RuntimeProbe();using var watcher=new FileSystemWatcher();MountManager.BindUploadWatcher(watcher,runtime.RequestScan);
        Assert.True(runtime.Take());runtime.Wake();Assert.False(runtime.Take());Assert.Equal(1,runtime.Signal.CurrentCount);runtime.Signal.Wait(0);
        var directory=System.IO.Path.GetFullPath(".");
        foreach(var (method,args) in new (string,EventArgs)[]{("OnChanged",new FileSystemEventArgs(WatcherChangeTypes.Changed,directory,"file")),("OnCreated",new FileSystemEventArgs(WatcherChangeTypes.Created,directory,"file")),("OnDeleted",new FileSystemEventArgs(WatcherChangeTypes.Deleted,directory,"file")),("OnRenamed",new RenamedEventArgs(WatcherChangeTypes.Renamed,directory,"new","old")),("OnError",new ErrorEventArgs(new InternalBufferOverflowException()))})
        {
            typeof(FileSystemWatcher).GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(watcher,[args]);runtime.Wake();Assert.True(runtime.Take());Assert.False(runtime.Take());Assert.Equal(1,runtime.Signal.CurrentCount);runtime.Signal.Wait(0);
        }
    }
    [Fact]
    public void ConcurrentSchedulerWakesCannotLoseRequestedScanAndLateEventsAreSafe()
    {
        using var runtime=new RuntimeProbe();Assert.True(runtime.Take());Parallel.Invoke(()=>Parallel.For(0,20000,_=>runtime.Wake()),()=>Parallel.For(0,20000,_=>runtime.RequestScan()));Assert.True(runtime.Take());Assert.False(runtime.Take());
        runtime.Stop();runtime.Dispose();runtime.Wake();runtime.RequestScan();Assert.False(runtime.Take());
    }
    private sealed class RuntimeProbe:IDisposable
    {
        private readonly IDisposable _runtime;
        internal readonly Action Wake,RequestScan,Stop;
        internal readonly Func<bool> Take;
        internal readonly SemaphoreSlim Signal;
        internal RuntimeProbe()
        {
            var type=typeof(MountManager).GetNestedType("UploadRuntime",BindingFlags.NonPublic)!;var value=Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,[CancellationToken.None],null)!;_runtime=(IDisposable)value;
            Wake=(Action)type.GetMethod("Wake")!.CreateDelegate(typeof(Action),value);RequestScan=(Action)type.GetMethod("RequestScan")!.CreateDelegate(typeof(Action),value);Stop=(Action)type.GetMethod("RequestStop")!.CreateDelegate(typeof(Action),value);Take=(Func<bool>)type.GetMethod("TakeScanRequest")!.CreateDelegate(typeof(Func<bool>),value);Signal=(SemaphoreSlim)type.GetField("Signal")!.GetValue(value)!;
        }
        public void Dispose()=>_runtime.Dispose();
    }
}
