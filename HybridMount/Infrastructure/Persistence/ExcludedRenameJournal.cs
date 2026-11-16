using HybridMount.Helpers.Paths;
using HybridMount.Models.Mounting.Renames;
using HybridMount.Infrastructure.Files;
using HybridMount.Models.Mounting;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
namespace HybridMount.Infrastructure.Persistence;

/// <summary>每挂载独立原子JSON；读回校验归属和路径，损坏时停止本功能，绝不重建日志或扩大操作范围。</summary>
internal sealed class ExcludedRenameJournal(string directory,Guid mount)
{
    private sealed record Document(int Schema,Guid Mount,ExcludedRenameIntent[] Items);
    private readonly string _file=Path.Combine(directory,"ExcludedRenames",mount.ToString("N")+".json");
    private readonly object _gate=new();private Document? _document;
    private ExcludedRenameIntent[]? _viewItems;
    private IReadOnlyList<ExcludedRenameIntent> _view = Array.Empty<ExcludedRenameIntent>();
    private IReadOnlyDictionary<string, ExcludedRenameIntent> _byId = new Dictionary<string, ExcludedRenameIntent>();
    internal static string CurrentOwner=>WindowsIdentity.GetCurrent().User?.Value??throw new IOException("无法取得当前账户身份。");
    private Document Load()
    {
        if(_document is not null)return _document;
        if(!File.Exists(_file))return _document=new(3,mount,[]);
        if(new FileInfo(_file).Length>16*1024*1024)throw new IOException("排除改名日志过长，保留原文件。");
        try
        {
            var doc=JsonSerializer.Deserialize<Document>(File.ReadAllText(_file),new JsonSerializerOptions{UnmappedMemberHandling=System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow})??throw new IOException("排除改名日志为空。");
            if(doc.Schema!=3||doc.Mount!=mount||doc.Items is null||doc.Items.Length>10000||doc.Items.Select(x=>x?.Id).Distinct().Count()!=doc.Items.Length)throw new IOException("排除改名日志格式无效。");
            foreach(var row in doc.Items)Validate(row);return _document=doc;
        }
        catch(Exception error)when(error is JsonException or ArgumentException){throw new IOException("排除改名日志损坏，未改写原记录。",error);}
    }
    private void Validate(ExcludedRenameIntent? row)
    {
        if(row is null||row.MountId!=mount||row.Owner!=CurrentOwner||!Guid.TryParseExact(row.Id,"N",out _)||row.Revision<1||row.SourceScope is not {Length:64}||string.IsNullOrEmpty(row.RootIdentity)||string.IsNullOrEmpty(row.FileId)||row.FileId.Length!=48||row.FileId.Any(x=>!Uri.IsHexDigit(x))||string.IsNullOrEmpty(row.RootPath)||!Path.IsPathFullyQualified(row.RootPath)||row.RootPath.StartsWith("\\",StringComparison.Ordinal))throw new IOException("排除改名日志身份无效。");
        foreach(var path in new[]{row.OldLocalPath,row.LocalPath,row.SourcePath,row.DestinationPath??row.LocalPath})if(string.IsNullOrEmpty(path)||RelativePaths.Normalize(path)!=path)throw new IOException("排除改名日志路径无效。");
        if(row.MoveReceipt is {} receipt&&(row.DestinationPath is null||receipt.Path!=row.DestinationPath||receipt.IsDirectory||receipt.Size<0||string.IsNullOrEmpty(receipt.Version)))throw new IOException("改名回执与目标不符。");
        if(row.RemoteMoved&&row.MoveReceipt is null||row.MappingRebound&&row.MoveReceipt is null)throw new IOException("改名恢复缺少目标回执。");
        if(row.Stage is not("Queued" or "WaitingUpload" or "Downloading" or "Localizing" or "PreparingMove" or "Moving" or "OutcomeUnknown" or "Copied" or "RemoteMoved" or "Completed" or "Canceled" or "Superseded" or "Failed" or "Conflict"))throw new IOException("排除改名日志阶段无效。");
    }
    private void Save(Document document)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);var temporary=_file+".tmp";
        try{using(var stream=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(stream,document);stream.Flush(true);}File.Move(temporary,_file,true);_document=document;}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    /// <summary>记录不可变且写入总是替换数组；同一内容复用只读视图，旧读者仍持有原快照。</summary>
    private void EnsureView(Document document)
    {
        if (ReferenceEquals(_viewItems, document.Items)) return;
        var view = Array.AsReadOnly(document.Items);
        var byId = document.Items.ToDictionary(row => row.Id, StringComparer.Ordinal);
        _view = view;
        _byId = byId;
        _viewItems = document.Items;
    }
    internal IReadOnlyList<ExcludedRenameIntent> Read()
    {
        lock (_gate)
        {
            EnsureView(Load());
            return _view;
        }
    }
    /// <summary>在同一已校验文档的内存索引定位ID；损坏日志继续抛错，不静默返回空日志。</summary>
    internal ExcludedRenameIntent? Find(string id)
    {
        lock (_gate)
        {
            EnsureView(Load());
            return _byId.GetValueOrDefault(id);
        }
    }
    /// <summary>仅首次身份意图插入；调用者不能用旧快照覆盖并发取消或较新的改名。</summary>
    internal ExcludedRenameIntent Add(ExcludedRenameIntent intent)
    {
        lock(_gate){Validate(intent);var doc=Load();var prior=doc.Items.FirstOrDefault(x=>x.Id==intent.Id);if(prior is not null)return prior;Save(doc with{Items=[..doc.Items,intent]});return intent;}
    }
    /// <summary>已发移动确认后，原子转交连续改名；旧源依赖解除，新目标仍独立受版本保护。</summary>
    internal ExcludedRenameIntent Continue(ExcludedRenameIntent captured,ExcludedRenameIntent next)
    {
        lock(_gate)
        {
            var doc=Load();var index=Array.FindIndex(doc.Items,x=>x.Id==captured.Id);if(index<0||doc.Items[index]!=captured)throw new IOException("连续改名意图已变化，等待下一轮核对。");
            Validate(next);var items=doc.Items.ToArray();items[index]=captured with{Stage="Superseded",Revision=checked(captured.Revision+1),ReadOnlyRequested=false,RetryRequested=false};
            Save(doc with{Items=[..items,next]});return next;
        }
    }
    /// <summary>短锁内读取最新代次再修改；可选完整快照比较为发送及退休提供提交边界。</summary>
    internal ExcludedRenameIntent? Update(string id,Func<ExcludedRenameIntent,ExcludedRenameIntent> change,ExcludedRenameIntent? expected=null)
    {
        lock(_gate){var doc=Load();var index=Array.FindIndex(doc.Items,x=>x.Id==id);if(index<0)return null;var old=doc.Items[index];if(expected is not null&&old!=expected)return null;var next=change(old);if(next==old)return old;next=next with{Revision=checked(old.Revision+1)};Validate(next);var items=doc.Items.ToArray();items[index]=next;Save(doc with{Items=items});return next;}
    }
}
