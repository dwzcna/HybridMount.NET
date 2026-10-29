using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

/// <summary>挂载根与操作路径的本机验证；不读取内容、不穿越普通重解析点。</summary>
public static class MountPathGuard
{
    public const string Product = "HybridMount.NET";
    public static string NormalizeRoot(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\",StringComparison.Ordinal)) throw new IOException("只支持本地 NTFS 目录。");
        var full=Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!),StringComparison.OrdinalIgnoreCase)) throw new IOException("不能挂载磁盘根。");
        var drive=new DriveInfo(Path.GetPathRoot(full)!);
        if (drive.DriveType is DriveType.Network or DriveType.Unknown or DriveType.NoRootDirectory || !drive.DriveFormat.Equals("NTFS",StringComparison.OrdinalIgnoreCase)) throw new IOException("目标必须位于本地 NTFS 卷。");
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("请先创建本地空目录。");
        for (var parent=new DirectoryInfo(full); parent is not null; parent=parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint)!=0) throw new IOException("挂载路径不能经过重解析点。");
        return full;
    }
    public static string FileIdentity(string path)
    {
        using var handle=CloudFilesNative.CreateFile(path,0x80,7,0,3,0x02200000,0);
        var bytes=new byte[24];
        if (handle.IsInvalid || !CloudFilesNative.GetFileId(handle,18,bytes,24)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return Convert.ToHexString(bytes);
    }
    public static bool IsUnder(string root,string path)
    {
        root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); path=Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return path.Equals(root,StringComparison.OrdinalIgnoreCase) || path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    }
    public static void ValidateOperation(MountDefinition mount,string path)
    {
        if (!IsUnder(mount.LocalPath,path) || mount.RootIdentity!=FileIdentity(mount.LocalPath)) throw new IOException("挂载路径或卷身份发生变化。");
        var full=Path.GetFullPath(path);
        for (var cursor=full; !cursor.Equals(mount.LocalPath,StringComparison.OrdinalIgnoreCase); cursor=Path.GetDirectoryName(cursor)!)
        {
            var attributes=File.GetAttributes(cursor);
            if ((attributes & FileAttributes.ReparsePoint)!=0 && !CloudFilesNative.IsCloudPath(cursor)) throw new IOException("不允许穿越重解析点。");
        }
        var identity=ReadRoot(full);
        if (identity?.MountId!=mount.Id || identity.Owner!=System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value) throw new IOException("目标不属于当前用户的本产品同步根。");
    }
    /// <summary>检查残余云文件；即使同步根注册已丢失也检查重解析标签，不穿越联接或符号链接。</summary>
    public static bool ContainsCloudPlaceholders(string directory)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes=File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint)!=0 && (CloudFilesNative.Inspect(path) is not null || CloudFilesNative.IsCloudPath(path))) return true;
            if ((attributes & (FileAttributes.Directory|FileAttributes.ReparsePoint))==FileAttributes.Directory && ContainsCloudPlaceholders(path)) return true;
        }
        return false;
    }
    public sealed record RootMarker(string Product,int Schema,Guid MountId,string Owner);
    public sealed record EntryMarker(Guid MountId,string EntryId);
    public static byte[] RootBytes(Guid id) => JsonSerializer.SerializeToUtf8Bytes(new RootMarker(Product,1,id,System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value));
    public static RootMarker? ReadRoot(string path)
    {
        try { var bytes=CloudFilesNative.RootIdentity(path); var marker=bytes is null ? null : JsonSerializer.Deserialize<RootMarker>(bytes); return marker is { Product:Product,Schema:1 } ? marker : null; }
        catch (JsonException) { return null; }
    }
    public static EntryMarker? ReadEntry(byte[] bytes) { try { return JsonSerializer.Deserialize<EntryMarker>(bytes); } catch (JsonException) { return null; } }
}
