namespace HybridMount.Models.Mounting.Windows;

/// <summary>原生属性保护期间通过领域检查点记录精确元数据边，不借出句柄或读取正文。</summary>
internal static class CloudUploadIssueProperty
{
    internal const int PropertyId=1;
    internal static async Task SetAsync(MountDefinition mount,LocalUpload upload,Func<LocalFileStamp,bool> begin,Action<LocalFileStamp,LocalFileStamp> commit,CancellationToken token,string message,Func<CancellationToken,Task>? written=null,Action<LocalFileStamp,LocalFileStamp>? proof=null)
    {
        var expected=upload.ObservedStamp.Split(':')[0];var attention=upload.Attention!;if(attention.PublishedIdentity is {} previous&&attention.ResolvedUtc is not null&&previous!=expected)throw new IOException("问题文件身份已变化，未修改替代文件的属性。");
        var text=message.Replace('\r',' ').Replace('\n',' ');if(text.Length>180)text=text[..177]+"…";
        var args=NativeRuntimeArguments.Root(mount,new{path=RelativePaths.ToLocal(mount.LocalPath,upload.LocalPath),expectedIdentity=expected,propertyId=PropertyId,text,icon=text.Length==0?"":ShellStatusIconResources.Issue});
        try{await(await NativeRuntimeClient.GetAsync().ConfigureAwait(false)).InvokeAsync("publishIssue",args,async(stage,data,cancel)=>
        {
            cancel.ThrowIfCancellationRequested();
            if(stage=="propertyGuarded")return new{proceed=begin(NativeRuntimeArguments.Stamp(data))};
            if(stage=="propertyBegin")return null;
            if(stage=="propertyWritten"){if(written is not null)await written(cancel).ConfigureAwait(false);return null;}
            if(stage=="propertyProof"){proof?.Invoke(NativeRuntimeProtocol.Read<LocalFileStamp>(data.GetProperty("before")),NativeRuntimeProtocol.Read<LocalFileStamp>(data.GetProperty("after")));return null;}
            if(stage=="propertyCommit"){commit(NativeRuntimeProtocol.Read<LocalFileStamp>(data.GetProperty("before")),NativeRuntimeProtocol.Read<LocalFileStamp>(data.GetProperty("after")));return null;}
            throw new IOException("原生属性阶段无效。");
        },token).ConfigureAwait(false);}
        catch(System.Runtime.InteropServices.COMException error)when(error.HResult==unchecked((int)0x800703ee)){throw new IOException("问题文件身份已变化，未修改替代文件的属性。",error);}
    }
}
