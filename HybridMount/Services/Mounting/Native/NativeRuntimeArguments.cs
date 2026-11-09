using System.Text.Json;
namespace HybridMount.Services.Mounting;

/// <summary>固定领域操作的根与条目授权；不会向原生端传递源URL、凭据或句柄。</summary>
internal static class NativeRuntimeArguments
{
    internal static Dictionary<string,object?> Root(MountDefinition mount,object? extra=null)
    {
        var result=new Dictionary<string,object?>{{"mountId",mount.Id},{"rootPath",mount.LocalPath},{"rootIdentity",mount.RootIdentity},{"name",mount.Name},{"version",typeof(NativeRuntimeArguments).Assembly.GetName().Version!.ToString()}};
        if(extra is not null)foreach(var item in NativeRuntimeProtocol.Element(extra).EnumerateObject())result[item.Name]=item.Value.Clone();return result;
    }
    internal static Dictionary<string,object?> Entry(MountDefinition mount,MountEntry entry,object? extra=null)
    {
        var result=Root(mount,new{path=RelativePaths.ToLocal(mount.LocalPath,entry.LocalPath),entryId=entry.Id,directory=entry.IsDirectory,@protected=entry.Protected,deleted=entry.Deleted});
        if(extra is not null)foreach(var item in NativeRuntimeProtocol.Element(extra).EnumerateObject())result[item.Name]=item.Value.Clone();return result;
    }
    internal static LocalFileStamp Stamp(JsonElement result)=>NativeRuntimeProtocol.Read<LocalFileStamp>(result.GetProperty("stamp"));
}
