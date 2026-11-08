using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

/// <summary>内部文件改名适配；在任何条件删源前持久保存目标回执，不扩展公共文件源契约。</summary>
internal interface IExcludedRenameSource
{
    Task<SourceEntry> MoveForExclusionAsync(string path,string destination,string version,Func<SourceEntry,Task> saveReceipt,CancellationToken token);
}
