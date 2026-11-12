using System.Diagnostics;
using System.Text.Json;

namespace HybridMount.Tests;

/// <summary>隔离原生映像的生产前台策略回归；注入时钟/身份，不切换用户窗口、不注册挂载或读取源。</summary>
[Collection("Native IPC")]
public sealed class ForegroundObserverTests
{
    private static object Step(string op,long now=0,params (string Name,object Value)[] values)
    {var step=new Dictionary<string,object>{{"op",op},{"now",now}};foreach(var (name,value) in values)step.Add(name,value);return step;}
    private static object Focus(long window,long now=0,long created=1)=>Step("focus",now,("window",window),("process",100),("thread",200),("created",created));
    private static object View(string directory,params string[] paths)=>new{owned=true,complete=true,directory,paths,directoryMs=2,sampleMs=3};
    private static object Complete(int ticket,long now,object result)=>Step("complete",now,("ticket",ticket),("result",result));
    private static object Frame(object view)=>new{kind="previewViews",views=new[]{view},diagnostics=new{enumerationComplete=true}};
    private static object Root(string session="one",string path=@"D:\isolated",bool reject=false)=>new{session,rootPath=path,reject};
    private static JsonElement At(JsonElement result,int index)=>result.GetProperty("outputs")[index];
    /// <summary>使用真实匿名管道和固定隔离角色；异常/取消也等待自己创建的进程退出。</summary>
    private static async Task<JsonElement> Run(object[] steps,object[]? roots=null)
    {
        var start=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"HybridMount.Stock.exe")){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        start.ArgumentList.Add("--test-foreground-observer");using var process=Process.Start(start)!;
        try{
            await IpcProtocol.WriteAsync(process.StandardInput.BaseStream,new{steps,roots=roots??[Root()]});
            var result=await IpcProtocol.ReadAsync<JsonElement>(process.StandardOutput.BaseStream,default).WaitAsync(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));Assert.Equal(0,process.ExitCode);return result.Clone();
        }finally{if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync();}}
    }
    [Fact(Timeout=15000)]
    public async Task DiagnosticsDoNotStartObserverOrTouchShell()
    {
        await using var runtime=await HybridMount.Services.Runtime.NativeRuntimeClient.StartOwnedForTestsAsync();
        var first=(await runtime.InvokeAsync("thumbnailDiagnostics",new{})).GetProperty("observer");
        var second=(await runtime.InvokeAsync("thumbnailDiagnostics",new{})).GetProperty("observer");
        Assert.Equal("foreground",first.GetProperty("scope").GetString());Assert.False(first.GetProperty("running").GetBoolean());Assert.True(JsonElement.DeepEquals(first,second));
    }
    [Fact(Timeout=15000)]
    public async Task NonExplorerForegroundNeverSchedulesAndForegroundDebounceIsBounded()
    {
        var result=await Run([Step("take",100000),Focus(10,100),Step("take",349),Step("take",350),Step("take",351)]);
        Assert.False(At(result,0).GetProperty("taken").GetBoolean());Assert.False(At(result,2).GetProperty("taken").GetBoolean());Assert.True(At(result,3).GetProperty("taken").GetBoolean());Assert.False(At(result,4).GetProperty("taken").GetBoolean());
        Assert.Equal("foreground",At(result,4).GetProperty("frame").GetProperty("diagnostics").GetProperty("scope").GetString());
    }
    [Fact(Timeout=15000)]
    public async Task TabNavigationClearsDemandAndRejectsOldResultEvenWithSameFrameAndFileName()
    {
        var result=await Run([Focus(10),Step("take",250),Complete(0,300,View(@"D:\isolated\a",@"D:\isolated\a\same.bmp")),Step("publish"),Step("navigate",350),Step("publish"),Complete(0,400,View(@"D:\isolated\a",@"D:\isolated\a\same.bmp")),Step("take",600),Complete(1,650,View(@"D:\isolated\b",@"D:\isolated\b\same.bmp")),Step("publish")]);
        var sent=result.GetProperty("sent");Assert.Equal(3,sent.GetArrayLength());Assert.Empty(sent[1].GetProperty("paths").EnumerateArray());Assert.Equal(@"D:\isolated\b\same.bmp",sent[2].GetProperty("paths")[0].GetString());
        Assert.False(At(result,6).GetProperty("accepted").GetBoolean());Assert.Equal(1,At(result,9).GetProperty("late").GetInt32());Assert.Equal(2,result.GetProperty("rootChecks").GetInt32());
    }
    [Fact(Timeout=15000)]
    public async Task FocusLossClearsOnceAndWindowReuseCannotAcceptPreviousGeneration()
    {
        var result=await Run([Focus(10),Step("take",250),Complete(0,300,View(@"D:\isolated",@"D:\isolated\x.bmp")),Step("publish"),Focus(0,400),Step("publish"),Step("publish"),Step("take",90000),Focus(10,100000,2),Complete(0,100001,View(@"D:\isolated",@"D:\isolated\x.bmp"))]);
        Assert.Equal(2,result.GetProperty("publications").GetInt32());Assert.Equal(1,result.GetProperty("rootChecks").GetInt32());Assert.False(At(result,7).GetProperty("taken").GetBoolean());Assert.False(At(result,9).GetProperty("accepted").GetBoolean());
    }
    [Fact(Timeout=15000)]
    public async Task SamplingFailureRetainsSameTargetDemandAndBacksOffFiveThenThirtySeconds()
    {
        var failed=new{complete=false,hresult=unchecked((int)0x800705B4),stage="viewport"};
        var result=await Run([Focus(10),Step("take",250),Complete(0,300,View(@"D:\isolated",@"D:\isolated\x.bmp")),Step("publish"),Step("change",500),Step("take",750),Complete(1,800,failed),Step("publish"),Step("take",5799),Step("take",5800),Complete(2,5850,failed),Step("publish"),Step("take",35849),Step("take",35850)]);
        Assert.Equal(1,result.GetProperty("publications").GetInt32());Assert.Equal(@"D:\isolated\x.bmp",At(result,7).GetProperty("value").GetProperty("paths")[0].GetString());
        Assert.False(At(result,8).GetProperty("taken").GetBoolean());Assert.True(At(result,9).GetProperty("taken").GetBoolean());Assert.False(At(result,12).GetProperty("taken").GetBoolean());Assert.True(At(result,13).GetProperty("taken").GetBoolean());
    }
    [Fact(Timeout=15000)]
    public async Task HeartbeatsAndUnchangedSnapshotsDoNotRevalidateRoots()
    {
        var frame=Frame(View(@"D:\isolated",@"D:\isolated\x.bmp"));var steps=new List<object>{Step("frame",0,("frame",frame))};
        for(var i=0;i<100;i++)steps.Add(Step("frame",i*250,("frame",new{kind="previewHeartbeat",diagnostics=new{samples=i}})));
        steps.Add(Step("frame",0,("frame",frame)));steps.Add(Step("clear"));steps.Add(Step("clear"));
        var roots=Enumerable.Range(0,40).Select(i=>Root(i.ToString(),i==0?@"D:\isolated":@"D:\unrelated"+i)).ToArray();var result=await Run(steps.ToArray(),roots);
        Assert.Equal(1,result.GetProperty("rootChecks").GetInt32());Assert.Equal(2,result.GetProperty("publications").GetInt32());
    }
    [Fact(Timeout=15000)]
    public async Task OrdinaryDirectoryWaitsThirtySecondsAndContentEventsCannotInvalidateIt()
    {
        var result=await Run([Focus(10),Step("take",250),Complete(0,300,new{complete=true,owned=false,filtered=true}),Step("event",1000,("event",0x8015),("inView",true)),Step("take",5300),Step("take",30299),Step("take",30300),Step("publish")]);
        Assert.Equal(0,At(result,3).GetProperty("action").GetInt32());Assert.False(At(result,4).GetProperty("taken").GetBoolean());Assert.False(At(result,5).GetProperty("taken").GetBoolean());Assert.True(At(result,6).GetProperty("taken").GetBoolean());Assert.Equal(0,result.GetProperty("rootChecks").GetInt32());
    }
    [Theory(Timeout=15000)]
    [InlineData(false,true,false)]
    [InlineData(true,false,false)]
    [InlineData(false,false,true)]
    public async Task BackgroundAndUnrelatedEventsCannotChangeForegroundGeneration(bool current,bool related,bool inView)
    {
        var result=await Run([Focus(10),Step("event",100,("event",0x800C),("current",current),("related",related),("inView",inView))]);
        Assert.Equal(At(result,0).GetProperty("generation").GetInt64(),At(result,1).GetProperty("generation").GetInt64());Assert.Equal(0,At(result,1).GetProperty("action").GetInt32());
    }
    [Fact(Timeout=15000)]
    public async Task ContinuousScrollCoalescesAndChangesDuringSampleAreNotLost()
    {
        var result=await Run([Focus(10),Step("take",250),Complete(0,300,View(@"D:\isolated",@"D:\isolated\x.bmp")),Step("change",500),Step("change",700),Step("take",750),Step("change",775),Complete(1,800,View(@"D:\isolated",@"D:\isolated\y.bmp")),Step("take",1025)]);
        Assert.True(At(result,5).GetProperty("taken").GetBoolean());Assert.Equal(1025,At(result,7).GetProperty("due").GetInt64());Assert.True(At(result,8).GetProperty("taken").GetBoolean());
    }
    [Fact(Timeout=15000)]
    public async Task FinishedBeforeDeadlineIsAcceptedAfterControllerWakeAndLateFinishRetries()
    {
        var result=await Run([Focus(10),Step("take",250),Complete(0,550,new{complete=true,owned=false,finishedTick=490}),Step("navigate",600),Step("take",850),Complete(1,1150,new{complete=true,owned=false,finishedTick=1101}),Step("take",6149),Step("take",6150)]);
        Assert.True(At(result,2).GetProperty("accepted").GetBoolean());Assert.False(At(result,5).GetProperty("accepted").GetBoolean());Assert.False(At(result,6).GetProperty("taken").GetBoolean());Assert.True(At(result,7).GetProperty("taken").GetBoolean());
    }
    [Fact(Timeout=15000)]
    public async Task IdentityFailureAndOutsideOrNestedHintsNeverPublishGeneration()
    {
        var result=await Run([Step("frame",0,("frame",Frame(View(@"D:\isolated",@"D:\outside\x.bmp",@"D:\isolated\nested\x.bmp",@"D:\isolated\x.txt")))),Step("frame",0,("frame",Frame(View(@"D:\isolated",@"D:\isolated\x.bmp"))))],[Root(reject:true)]);
        Assert.Equal(1,result.GetProperty("rootChecks").GetInt32());Assert.Equal(0,result.GetProperty("publications").GetInt32());
    }
}
