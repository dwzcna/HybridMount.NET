using System.Text.Json;
using HybridMount.Services.Runtime;

namespace HybridMount.Tests;

/// <summary>随机独立MSIX中的无根启动验收，复用生产Stock和严格私有握手，不注册CFAPI或COM。</summary>
public static class NativeChildLaunchProbe
{
    public static async Task<int> RunAsync(string output)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        HostDiagnosticStore.ChildIdentityDirectoryForTests=Path.Combine(Path.GetDirectoryName(output)!,"HostDiagnostics");
        var report=new Dictionary<string,object?>();
        try
        {
            report["Packaged"]=NativeProcessIdentity.IsPackaged;
            await using(var runtime=await NativeRuntimeClient.StartOwnedForTestsAsync())
            {
                using var child=System.Diagnostics.Process.GetProcessById(runtime.ProcessId);
                var proof=NativeProcessIdentity.Verify(child,Path.Combine(AppContext.BaseDirectory,"HybridMount.Stock.exe"),"NativeRuntime");
                if(!NativeProcessIdentity.IsPackaged||!proof.Matches||!runtime.IsAvailable)throw new IOException("实际隔离包身份或握手未通过。");
                try{NativeProcessIdentity.Verify(child,Path.Combine(AppContext.BaseDirectory,"unexpected.exe"),"NativeRuntime");throw new InvalidOperationException("错误映像未被拒绝。");}
                catch(IOException){report["WrongImageRejected"]=true;}
                report["Identity"]=proof;report["Handshake"]=true;report["ProcessId"]=child.Id;
            }
            report["Drained"]=true;report["Success"]=true;return 0;
        }
        catch(Exception error){report["Success"]=false;report["Error"]=error.ToString();return 1;}
        finally{await File.WriteAllTextAsync(output,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));}
    }
}
