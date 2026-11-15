using HybridMount.Models.Runtime;
using HybridMount.Models.Mounting;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace HybridMount.Commons.Windows;

/// <summary>Windows接入层的进程边界；当前及后续方法均返回模型和回执，不借出原生指针。</summary>
internal interface ICloudFilesRuntime
{
    /// <summary>只执行原生端固定允许的完整领域操作；检查点保存意图，失败返回原代码，不携带裸句柄。</summary>
    Task<JsonElement> InvokeAsync(string operation,object data,Func<string,JsonElement,CancellationToken,Task<object?>>? checkpoint=null,CancellationToken token=default);
    Task<NativeFileSnapshot> InspectAsync(string path,bool persisted=false,bool ranges=false,string? expectedIdentity=null,CancellationToken token=default);
    Task<JsonElement> RootInfoAsync(string path,CancellationToken token=default);
}
