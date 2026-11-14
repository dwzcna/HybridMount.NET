using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HybridMount.Services;

/// <summary>只由当前运行模式生成的清理范围；路径不可编辑，不包含共享父目录、另一模式或程序目录。</summary>
internal sealed class SettingsResetScope
{
    internal bool Packaged {get;}
    internal string Mode=>Packaged?"Packaged":"Unpackaged";
    internal string RegistryPath=>ApplicationPaths.RegistryPathFor(Packaged);
    internal string ProductScope=>"HybridMount.NET."+Mode;
    internal IReadOnlyList<string> Directories {get;}
    internal string MountDataDirectory {get;}
    private SettingsResetScope(bool packaged,string[] directories,string mountDirectory){Packaged=packaged;Directories=Array.AsReadOnly(directories);MountDataDirectory=mountDirectory;}
    /// <summary>由系统LocalAppData和已核验包族名推导白名单；测试可传隔离LocalAppData，不接收任意删除目录。</summary>
    internal static SettingsResetScope Create(bool packaged,string localAppData,string? packageFamily=null)
    {
        if(!Path.IsPathFullyQualified(localAppData)||localAppData.StartsWith(@"\\",StringComparison.Ordinal))throw new IOException(global::HybridMount.Services.Localization.UiText.Text("Text_C8CADF5A6F81"));
        var physical=Path.Combine(Path.GetFullPath(localAppData),"HybridMount.NET",packaged?"Packaged":"Unpackaged");
        if(!packaged)return new(false,[physical],Path.Combine(physical,"Mounting"));
        if(string.IsNullOrWhiteSpace(packageFamily)||packageFamily is "." or ".."||packageFamily.IndexOfAny(Path.GetInvalidFileNameChars())>=0||packageFamily.Contains('/')||packageFamily.Contains('\\'))throw new IOException(global::HybridMount.Services.Localization.UiText.Text("Text_C8CADF5A6F81"));
        var package=Path.Combine(Path.GetFullPath(localAppData),"Packages",packageFamily);
        // 删除本应用的数据目录本身；包族父目录及Windows维护的包运行状态不属于应用设置。
        return new(true,[physical,Path.Combine(package,"LocalState"),Path.Combine(package,"LocalCache"),Path.Combine(package,"RoamingState"),Path.Combine(package,"TempState")],Path.Combine(package,"LocalState","Mounting"));
    }
    internal bool SameAs(SettingsResetScope other)=>Packaged==other.Packaged&&Directories.SequenceEqual(other.Directories,StringComparer.OrdinalIgnoreCase);
    internal static bool Overlaps(string first,string second)
    {
        first=Path.TrimEndingDirectorySeparator(Path.GetFullPath(first));second=Path.TrimEndingDirectorySeparator(Path.GetFullPath(second));
        return string.Equals(first,second,StringComparison.OrdinalIgnoreCase)||first.StartsWith(Path.EndsInDirectorySeparator(second)?second:second+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||second.StartsWith(Path.EndsInDirectorySeparator(first)?first:first+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    }
}
internal sealed record SettingsResetDirectoryProof(string Path,string? Identity);
internal sealed record SettingsResetPlan(SettingsResetScope Scope,IReadOnlyList<SettingsResetDirectoryProof> Directories);
/// <summary>预检的正常阻止结果没有删除授权；真实查询失败仍由异常报告。</summary>
internal sealed record SettingsResetInspection(SettingsResetPlan? Plan,string? BlockedReason=null);
/// <summary>先检查注册阻止条件，再捕获删除范围；被阻止时不扫描应用数据。</summary>
internal static class SettingsResetPreflight
{
    internal static SettingsResetInspection Inspect(SettingsResetScope scope,Func<string?> blocker,
        Func<IReadOnlyList<SettingsResetDirectoryProof>> capture,Action verifyRecovery)
    {
        if(blocker() is {} reason)return new(null,reason);
        var proofs=capture();verifyRecovery();return new(new(scope,proofs));
    }
}

/// <summary>只遍历现有本地元数据；固定目录身份，不跟随重解析链接，删除时逐层使用非递归删除。</summary>
internal static class SettingsResetDirectories
{
    /// <summary>后台线程只读采样全部目录；查询故障、链接或取消直接失败，未执行任何删除。</summary>
    internal static IReadOnlyList<SettingsResetDirectoryProof> Capture(SettingsResetScope scope,CancellationToken token)
        =>Array.AsReadOnly(scope.Directories.Select(path=>new SettingsResetDirectoryProof(path,Inspect(path,token))).ToArray());
    internal static void Validate(SettingsResetPlan plan,CancellationToken token)
    {
        if(!plan.Directories.Select(x=>x.Path).SequenceEqual(plan.Scope.Directories,StringComparer.OrdinalIgnoreCase))throw new IOException(global::HybridMount.Services.Localization.UiText.Text("Text_E122A99B231B"));
        foreach(var proof in plan.Directories)if(Inspect(proof.Path,token)!=proof.Identity)throw new IOException(global::HybridMount.Services.Localization.UiText.Text("Text_ABA53AA1CD13",proof.Path));
    }
    private static string? Inspect(string path,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();using var ancestors=PinAncestors(path);
        return InspectTree(path,token);
    }
    private static string? InspectTree(string path,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();using var pin=OpenDirectory(path,out var identity);
        if(pin is null)return null;
        foreach(var child in Directory.EnumerateFileSystemEntries(path))
        {
            token.ThrowIfCancellationRequested();var attributes=File.GetAttributes(child);RejectReparse(child,attributes);
            if((attributes&FileAttributes.Directory)!=0)InspectTree(child,token);
        }
        return identity;
    }
    /// <summary>生产调用前已停写并复核全部范围；不清理父目录，任何单项失败均停止并保留后续项目。</summary>
    internal static void Delete(SettingsResetDirectoryProof proof)
    {
        using var ancestors=PinAncestors(proof.Path);
        DeleteTree(proof.Path,proof.Identity,true);
    }
    private static void DeleteTree(string path,string? expected,bool verify)
    {
        using(var pin=OpenDirectory(path,out var identity))
        {
            if(verify&&identity!=expected)throw new IOException(global::HybridMount.Services.Localization.UiText.Text("Text_ABA53AA1CD13",path));
            if(pin is null)return;
            foreach(var child in Directory.EnumerateFileSystemEntries(path))
            {
                var attributes=File.GetAttributes(child);RejectReparse(child,attributes);
                if((attributes&FileAttributes.Directory)!=0)DeleteTree(child,null,false);
                else DeleteLocalFile(child);
            }
        }
        // 非递归删除不会遍历替换后的链接；父目录句柄在整个操作期间禁止重命名和删除。
        Directory.Delete(path,false);
    }
    /// <summary>固定文件句柄后标记删除；不跟随链接，忽略只读位但不改共享硬链接的属性或正文。</summary>
    private static void DeleteLocalFile(string path)
    {
        using var handle=CreateFile(path,0x10080,3,0,3,0x02200000,0);
        if(handle.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error(),path);
        if(!GetFileInformationByHandle(handle,out var info))throw new Win32Exception(Marshal.GetLastWin32Error(),path);
        RejectReparse(path,(FileAttributes)info.Attributes);
        if((info.Attributes&0x10)!=0)throw new IOException(global::HybridMount.Services.Localization.UiText.Text("Text_9E9FE4E789B7",path));
        uint disposition=0x11; // FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE，最低系统为Windows 11。
        if(!SetFileInformationByHandle(handle,21,ref disposition,sizeof(uint)))throw new Win32Exception(Marshal.GetLastWin32Error(),path);
    }
    private static void RejectReparse(string path,FileAttributes attributes)
    {if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException(global::HybridMount.Services.Localization.UiText.Text("Text_CBE71FBB20D3",path));}
    private static SafeFileHandle? OpenDirectory(string path,out string? identity)
    {
        identity=null;var handle=CreateFile(path,0x80,3,0,3,0x02200000,0);
        if(handle.IsInvalid)
        {
            var error=Marshal.GetLastWin32Error();handle.Dispose();if(error is 2 or 3)return null;
            throw new Win32Exception(error,global::HybridMount.Services.Localization.UiText.Text("Text_9E9FE4E789B7",path));
        }
        try
        {
            if(!GetFileInformationByHandle(handle,out var info))throw new Win32Exception(Marshal.GetLastWin32Error(),global::HybridMount.Services.Localization.UiText.Text("Text_9E9FE4E789B7",path));
            RejectReparse(path,(FileAttributes)info.Attributes);
            if((info.Attributes&0x10)==0)throw new IOException(global::HybridMount.Services.Localization.UiText.Text("Text_9E9FE4E789B7",path));
            identity=$"{info.Volume:X8}:{info.IndexHigh:X8}{info.IndexLow:X8}";return handle;
        }
        catch{handle.Dispose();throw;}
    }
    private static IDisposable PinAncestors(string path)
    {
        var pins=new Pins();try
        {
            var ancestors=new Stack<string>();for(var parent=Path.GetDirectoryName(path);parent is not null;parent=Path.GetDirectoryName(parent))ancestors.Push(parent);
            while(ancestors.TryPop(out var parent)){var handle=OpenDirectory(parent,out _);if(handle is not null)pins.Handles.Add(handle);}
            return pins;
        }catch{pins.Dispose();throw;}
    }
    private sealed class Pins:IDisposable
    {
        internal readonly List<SafeFileHandle> Handles=[];
        public void Dispose(){foreach(var handle in Handles.AsEnumerable().Reverse())handle.Dispose();Handles.Clear();}
    }
    [StructLayout(LayoutKind.Sequential)]private struct FileInformation
    {public uint Attributes,CreationLow,CreationHigh,AccessLow,AccessHigh,WriteLow,WriteHigh,Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;}
    [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateFile(string path,uint access,uint share,nint security,uint disposition,uint flags,nint template);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetFileInformationByHandle(SafeFileHandle handle,int kind,ref uint information,uint size);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetFileInformationByHandle(SafeFileHandle handle,out FileInformation info);
}
