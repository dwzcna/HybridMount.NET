using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using HybridMount.Models.Mounting;
using HybridMount.Models.Mounting.Windows;
using HybridMount.Services.Runtime;

namespace HybridMount.Tests;

/// <summary>显式独立MSIX菜单注册对照；仅创建本轮GUID根，读取真实Shell菜单，不执行菜单或连接远端源。</summary>
public static unsafe class CloudFilesMenuProbe
{
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    /// <summary>在调用方STA执行本轮包验证并有序清理；失败保留结果和精确根证据，不触及现有安装。</summary>
    public static int Run(string area)
    {
        var report=new Dictionary<string,object?>();MountDefinition? mount=null;string? session=null;var cleanup=false;var success=false;
        try{
            var package=global::Windows.ApplicationModel.Package.Current;Assert.StartsWith("dwzcn.HybridMount.NativePreview.Probe",package.Id.Name);report["Package"]=package.Id.FullName;
            NativeThumbnailBindingProbe.RegisterForUninstallAsync(area).GetAwaiter().GetResult();
            using(var proof=JsonDocument.Parse(File.ReadAllText(Path.Combine(area,"uninstall-root.json"))))mount=proof.RootElement.GetProperty("Root").Deserialize<MountDefinition>()!;
            Assert.StartsWith(Path.GetFullPath(area)+Path.DirectorySeparatorChar,Path.GetFullPath(mount.LocalPath),StringComparison.OrdinalIgnoreCase);report["Root"]=mount;
            var runtime=NativeRuntimeClient.Get();session=Guid.NewGuid().ToString("N");runtime.Invoke("connectRoot",NativeRuntimeArguments.Root(mount,new{sessionId=session,blockHydration=true}));
            var cold=Path.Combine(mount.LocalPath,"cold.bin");var folder=Path.Combine(mount.LocalPath,"folder");var ordinary=Path.Combine(mount.LocalPath,"ordinary.txt");
            foreach(var item in new[]{(Path:cold,Id:"menu-cold",Directory:false,Size:1024L),(Path:folder,Id:"menu-folder",Directory:true,Size:0L)})runtime.Invoke("createPlaceholder",NativeRuntimeArguments.Root(mount,new{sessionId=session,path=item.Path,entryId=item.Id,directory=item.Directory,size=item.Size,time=DateTime.UtcNow.ToFileTimeUtc()}));
            File.WriteAllText(ordinary,"isolated menu probe");var outside=Directory.CreateDirectory(Path.Combine(area,"ordinary")).FullName;var outsideFile=Path.Combine(outside,"outside.txt");File.WriteAllText(outsideFile,"isolated menu probe");
            var before=Facts(cold);var rows=new Dictionary<string,object>{["placeholder"]=Query([cold]),["placeholderDirectory"]=Query([folder]),["newOrdinaryFile"]=Query([ordinary]),["mixedSelection"]=Query([cold,ordinary]),["rootSelection"]=Query([mount.LocalPath]),["directoryBackground"]=Query([],mount.LocalPath),["ordinaryOutside"]=Query([outsideFile])};
            report["Menus"]=rows;report["Before"]=before;report["After"]=Facts(cold);Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(before),JsonSerializer.SerializeToElement(report["After"])));report["NoRemoteSourceConfigured"]=true;
            File.Delete(outsideFile);Directory.Delete(outside);success=true;
        }catch(Exception error){report["Error"]=error.ToString();}
        finally{
            if(mount is not null)try{
                var runtime=NativeRuntimeClient.Get();if(session is not null){runtime.Invoke("clearRoot",NativeRuntimeArguments.Root(mount,new{sessionId=session}));
                    foreach(var name in new[]{"cold.bin","ordinary.txt"}){var path=Path.Combine(mount.LocalPath,name);if(File.Exists(path))File.Delete(path);}var folder=Path.Combine(mount.LocalPath,"folder");if(Directory.Exists(folder)){Assert.Empty(Directory.EnumerateFileSystemEntries(folder));Directory.Delete(folder);}
                    runtime.Invoke("disconnectRoot",NativeRuntimeArguments.Root(mount,new{sessionId=session}));}
                CloudRootRegistration.Unregister(mount);var zero=Path.Combine(mount.LocalPath,"zero.bin");if(File.Exists(zero)){Assert.Equal(0,new FileInfo(zero).Length);File.Delete(zero);}Assert.Empty(Directory.EnumerateFileSystemEntries(mount.LocalPath));Directory.Delete(mount.LocalPath);cleanup=true;
            }catch(Exception error){report["CleanupError"]=error.ToString();}
            NativeRuntimeClient.StopSharedAsync().GetAwaiter().GetResult();report["Cleanup"]=cleanup;report["Passed"]=success&&cleanup;File.WriteAllText(Path.Combine(area,"menu-registration.json"),JsonSerializer.Serialize(report,Json));
        }
        return success&&cleanup?0:1;
    }
    private static object Facts(string path){var value=CloudFilesNative.InspectPersisted(path);return new{Identity=Convert.ToBase64String(value.Identity),value.Info.PinState,value.Info.InSyncState,value.Info.OnDiskSize,value.Info.ModifiedSize};}
    /// <summary>真实IShellFolder/IContextMenu查询；背景、单项及混选分别绑定，不打开Explorer窗口。</summary>
    private static object Query(string[] paths,string? background=null)
    {
        var initialized=CoInitializeEx(0,2);nint folder=0,menu=0,item=0;var pidls=new nint[paths.Length];var times=new List<double>();var values=new List<string>();var code=0;
        try{
            var folderId=new Guid("000214E6-0000-0000-C000-000000000046");var menuId=new Guid("000214E4-0000-0000-C000-000000000046");
            if(background is not null){var itemId=new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");var handler=new Guid("3981E224-F559-11D3-8E3A-00C04F6837D5");Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(background,0,in itemId,out item));Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint,nint,Guid*,Guid*,nint*,int>)Table(item)[3])(item,0,&handler,&folderId,&folder));Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint,nint,Guid*,nint*,int>)Table(folder)[8])(folder,0,&menuId,&menu));}
            else{
                for(var index=0;index<paths.Length;index++)Marshal.ThrowExceptionForHR(SHParseDisplayName(paths[index],0,out pidls[index],0,out _));
                Marshal.ThrowExceptionForHR(SHBindToParent(pidls[0],in folderId,out folder,out _));var children=pidls.Select(ILFindLastID).ToArray();uint reserved=0;
                fixed(nint* child=children)Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint,nint,uint,nint*,Guid*,uint*,nint*,int>)Table(folder)[10])(folder,0,(uint)children.Length,child,&menuId,&reserved,&menu));
            }
            for(var pass=0;pass<6;pass++){
                var popup=CreatePopupMenu();var started=Stopwatch.GetTimestamp();try{code=((delegate* unmanaged[Stdcall]<nint,nint,uint,uint,uint,uint,int>)Table(menu)[3])(menu,popup,0,1,0x7fff,0);values.Clear();if(code>=0)ReadMenu(popup,values,0);}
                finally{times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);DestroyMenu(popup);}if(values.Any(x=>x.Contains("HybridMount",StringComparison.Ordinal)))break;Thread.Sleep(25);
            }
        }catch(Exception error){code=error.HResult;values.Add(error.GetType().Name);}
        finally{if(menu!=0)Marshal.Release(menu);if(folder!=0)Marshal.Release(folder);if(item!=0)Marshal.Release(item);foreach(var pidl in pidls)if(pidl!=0)Marshal.FreeCoTaskMem(pidl);if(initialized>=0)CoUninitialize();}
        return new{HResult=code,Titles=values,ProductEntries=values.Count(x=>x.Contains("HybridMount",StringComparison.Ordinal)),QueryMilliseconds=times,Surface="IShellFolder/IContextMenu; not a Win11 modern-menu UI measurement"};
    }
    private static void ReadMenu(nint menu,List<string> values,int depth){if(depth>5)return;for(var index=0;index<Math.Min(GetMenuItemCount(menu),128);index++){var text=new StringBuilder(512);GetMenuString(menu,(uint)index,text,512,0x400);if(text.Length>0)values.Add(text.ToString());var nested=GetSubMenu(menu,index);if(nested!=0)ReadMenu(nested,values,depth+1);}}
    private static void** Table(nint value)=>*(void***)value;
    [DllImport("ole32.dll")]private static extern int CoInitializeEx(nint reserved,uint flags);
    [DllImport("ole32.dll")]private static extern void CoUninitialize();
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern int SHCreateItemFromParsingName(string path,nint context,in Guid id,out nint value);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern int SHParseDisplayName(string path,nint context,out nint pidl,uint flags,out uint attributes);
    [DllImport("shell32.dll")]private static extern int SHBindToParent(nint pidl,in Guid id,out nint folder,out nint child);
    [DllImport("shell32.dll")]private static extern nint ILFindLastID(nint pidl);
    [DllImport("user32.dll")]private static extern nint CreatePopupMenu();
    [DllImport("user32.dll")]private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")]private static extern int GetMenuItemCount(nint menu);
    [DllImport("user32.dll",EntryPoint="GetMenuStringW",CharSet=CharSet.Unicode)]private static extern int GetMenuString(nint menu,uint index,StringBuilder text,int length,uint flags);
    [DllImport("user32.dll")]private static extern nint GetSubMenu(nint menu,int index);
}
