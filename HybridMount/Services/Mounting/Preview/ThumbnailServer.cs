using System.Runtime.InteropServices;
namespace HybridMount.Services.Mounting;

/// <summary>先于WinUI的独立COM缩略图宿主；只读对应挂载库并投递有界生成请求，不创建挂载、凭据或协议服务。</summary>
internal static class ThumbnailServer
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int ReadThumbnail(nint context,[MarshalAs(UnmanagedType.LPWStr)]string path,uint size,out nint png,out uint length);
    [DllImport("HybridMount.Ext.dll",CallingConvention=CallingConvention.StdCall)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory|DllImportSearchPath.System32)]
    private static extern int HybridMountRunShellServer(ReadThumbnail read,nint context,nint stop,uint idleMilliseconds,uint flags);
    internal static bool IsEntry(string[] args)=>args.Length>0&&args[0] is "--thumbnail-server" or "--shell-server";
    internal static bool ValidArguments(string[] args)=>IsEntry(args)&&(args.Length==1||args.Length==2&&(args[1].Equals("-Embedding",StringComparison.OrdinalIgnoreCase)||args[1].Equals("/Embedding",StringComparison.OrdinalIgnoreCase)));
    /// <summary>参数不接受任意文件／数据库／DLL路径，缓存miss不返回通用图标成功值。</summary>
    internal static int Run(string[] args)
    {
        if(!ValidArguments(args))return 2;
        try
        {
            var reader=new ThumbnailCacheReader(ThumbnailCachePaths.DirectoryPath,true);var requests=new LocalPreviewRequestClient(args[0]=="--shell-server"?"requestThumbnail":"requestLocalPreview");
            using var stopped=new EventWaitHandle(false,EventResetMode.ManualReset);
            ReadThumbnail callback=(nint context,string path,uint size,out nint png,out uint length)=>
            {
                png=0;length=0;
                try
                {
                    var bytes=reader.TryGet(path,checked((int)size));
                    if(bytes is null){requests.Queue(path,(int)size);return unchecked((int)0x80070490);}
                    png=Marshal.AllocCoTaskMem(bytes.Length);Marshal.Copy(bytes,0,png,bytes.Length);length=(uint)bytes.Length;return 0;
                }
                catch{if(png!=0)Marshal.FreeCoTaskMem(png);png=0;length=0;return unchecked((int)0x80004005);}
            };
            try{return HybridMountRunShellServer(callback,0,stopped.SafeWaitHandle.DangerousGetHandle(),30000,args[0]=="--shell-server"?3u:1u)<0?1:0;}
            finally{GC.KeepAlive(callback);requests.DisposeAsync().AsTask().GetAwaiter().GetResult();}
        }
        catch{return 1;}
    }
}
