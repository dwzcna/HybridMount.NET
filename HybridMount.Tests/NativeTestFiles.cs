using HybridMount.Services.Mounting;
namespace HybridMount.Tests;
/// <summary>仅隔离测试映像接受的文件状态设定；逐次验证本产品根、当前账户及精确文件ID。</summary>
internal static class NativeTestFiles
{
    internal static Task SetPropertiesAsync(string path,params ShellCustomProperty[] properties)=>NativeRuntimeClient.Get().InvokeAsync("testSetCustomStates",new{path,expectedIdentity=LocalFileState.Read(path).Identity,properties=properties.Select(item=>new{id=item.Id,value=item.Value,icon=item.Icon}).ToArray()});
    internal static void Pin(string path,uint state)=>NativeRuntimeClient.Get().Invoke("testSetPin",new{path,expectedIdentity=LocalFileState.Read(path).Identity,pin=state});
    internal static void Dehydrate(string path,long offset=0,long length=-1)=>NativeRuntimeClient.Get().Invoke("testDehydrate",new{path,expectedIdentity=LocalFileState.Read(path).Identity,offset,length});
}
