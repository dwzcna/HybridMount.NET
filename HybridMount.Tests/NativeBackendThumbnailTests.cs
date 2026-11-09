using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using HybridMount.Services.Mounting;
using Microsoft.Data.Sqlite;
namespace HybridMount.Tests;

/// <summary>纯原生进程直读真实隔离根的缓存；不启动主应用，不改变正式根或正式缓存。</summary>
[Collection("Native IPC")]
public sealed class NativeBackendThumbnailTests
{
    [Fact(Timeout=90000)]
    public async Task ColdCacheHitAfterDisconnectNeedsNoManagedHostOrRemoteRead()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false);
        test.Dav.Put("native-cache.bmp",PreviewPipelineTests.Bmp());await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));
        var mount=test.Manager.Mounts.Single(x=>x.Definition.Id==test.Id).Definition;
        var root=ThumbnailCacheReader.RootFor(mount with{ThumbnailsEnabled=true});var store=new ThumbnailCacheStore(ThumbnailCachePaths.ForMount(test.Id));
        root=store.Configure(root,true);
        try
        {
            var entry=test.Repository.Entries(test.Id).Single(x=>x.LocalPath=="native-cache.bmp");
            var binding=ThumbnailCacheReader.Capture(root,entry);Assert.NotNull(binding);
            var png=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Assets","SyncIssue.png"));Assert.True(store.Publish(binding,new Dictionary<int,byte[]>{{64,png}}));
            await test.Manager.DisconnectAsync(test.Id);var requests=test.Dav.Server.Requests.Count;
            var before=LocalFileState.Read(binding.Path);var info=CloudFilesNative.InspectPersisted(binding.Path);
            var result=await Read(binding.Path);Assert.True(result.GetProperty("hit").GetBoolean(),result.ToString());Assert.Equal(png.Length,result.GetProperty("bytes").GetInt32());Assert.Equal(Convert.ToHexString(SHA256.HashData(png)),result.GetProperty("sha256").GetString());
            Assert.Equal(before,LocalFileState.Read(binding.Path));var after=CloudFilesNative.InspectPersisted(binding.Path);Assert.Equal(info.Identity,after.Identity);Assert.Equal(info.Info.PinState,after.Info.PinState);Assert.Equal(info.Info.InSyncState,after.Info.InSyncState);Assert.Equal(info.Info.OnDiskSize,after.Info.OnDiskSize);Assert.Equal(info.Info.ModifiedSize,after.Info.ModifiedSize);Assert.Equal(requests,test.Dav.Server.Requests.Count);
            store.Configure(root with{Enabled=false});Assert.False((await Read(binding.Path)).GetProperty("hit").GetBoolean());
            root=store.Configure(root with{Enabled=true});Assert.False((await Read(binding.Path)).GetProperty("hit").GetBoolean());
        }
        finally{store.DeleteDatabase(store.Root(test.Id));}
    }
    [Fact(Timeout=90000)]
    public async Task NativeOutOfProcessComReturnsCachedBitmapAfterMainMountDisconnects()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);test.Dav.Put("com-cache.bmp",PreviewPipelineTests.Bmp());await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));
        var mount=test.Manager.Mounts.Single().Definition;var entry=test.Repository.Entries(test.Id).Single(item=>item.LocalPath=="com-cache.bmp");var path=Path.Combine(test.Local,"com-cache.bmp");var store=new ThumbnailCacheStore(ThumbnailCachePaths.ForMount(test.Id));var root=store.Configure(ThumbnailCacheReader.RootFor(mount with{ThumbnailsEnabled=true}),true);
        try
        {
            var png=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Assets","SyncIssue.png"));Assert.True(store.Publish(ThumbnailCacheReader.Capture(root,entry)!,new Dictionary<int,byte[]>{{64,png}}));await test.Manager.DisconnectAsync(test.Id);var requests=test.Dav.Server.Requests.Count;
            var rootDirectory=new DirectoryInfo(AppContext.BaseDirectory);while(rootDirectory is not null&&!File.Exists(Path.Combine(rootDirectory.FullName,"Directory.Build.props")))rootDirectory=rootDirectory.Parent;Assert.NotNull(rootDirectory);
#if DEBUG
            const string configuration="Debug";
#else
            const string configuration="Release";
#endif
            var isolated=Path.Combine(test.Dav.Area,"native-shell");Directory.CreateDirectory(isolated);foreach(var file in new[]{"HybridMount.Native.exe","e_sqlite3.dll"})File.Copy(Path.Combine(AppContext.BaseDirectory,file),Path.Combine(isolated,file));
            File.Copy(Path.Combine(rootDirectory.FullName,"artifacts","native-thumbnail-host",System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture==System.Runtime.InteropServices.Architecture.Arm64?"ARM64":"x64",configuration,"HybridMount.Ext.dll"),Path.Combine(isolated,"HybridMount.Ext.dll"));
            var start=Start("--shell-server");start.FileName=Path.Combine(isolated,"HybridMount.Native.exe");using var process=Process.Start(start)!;
            try
            {
                var until=DateTime.UtcNow.AddSeconds(10);NativePreviewInterop? provider=null;
                while(provider is null)try{provider=new NativePreviewInterop(path,true,null,true,new Guid("d0037037-4d1c-4c7e-a519-34c76b9bb281"));}catch(System.Runtime.InteropServices.COMException error)when(error.HResult==unchecked((int)0x80040154)&&DateTime.UtcNow<until){await Task.Delay(50);}
                using(provider){Assert.Equal(0,provider.Thumbnail(64,out var alpha,out var bitmap));try{Assert.Equal(2u,alpha);Assert.InRange(NativePreviewInterop.BitmapWidth(bitmap),1,64);}finally{NativePreviewInterop.DeleteObject(bitmap);}}
                process.Refresh();Assert.DoesNotContain(process.Modules.Cast<ProcessModule>(),module=>module.ModuleName.Equals("coreclr.dll",StringComparison.OrdinalIgnoreCase));Assert.Equal(requests,test.Dav.Server.Requests.Count);Assert.False(CloudFilesNative.HasFullContent(path,new FileInfo(path).Length));
            }
            finally{if(!process.HasExited){process.Kill();await process.WaitForExitAsync();}}
        }
        finally{store.DeleteDatabase(store.Root(test.Id));}
    }
    [Fact(Timeout=90000)]
    public async Task MissingAndNewerCacheNeverCreatesOrRepairsDatabase()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);test.Dav.Put("missing.bmp",PreviewPipelineTests.Bmp());await WebDavTests.Finished(test.Manager,test.Manager.Queue(test.Id,"refresh"));
        var path=Path.Combine(test.Local,"missing.bmp");var database=ThumbnailCachePaths.ForMount(test.Id);Assert.False(File.Exists(database));Assert.False((await Read(path)).GetProperty("hit").GetBoolean());Assert.False(File.Exists(database));
        var mount=test.Manager.Mounts.Single(x=>x.Definition.Id==test.Id).Definition;var store=new ThumbnailCacheStore(database);var root=store.Configure(ThumbnailCacheReader.RootFor(mount with{ThumbnailsEnabled=true}),true);
        try
        {
            using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=database,Pooling=false}.ToString())){db.Open();using var cmd=db.CreateCommand();cmd.CommandText="PRAGMA user_version=99";cmd.ExecuteNonQuery();}
            Assert.False((await Read(path)).GetProperty("hit").GetBoolean());
            using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=database,Pooling=false}.ToString())){db.Open();using var cmd=db.CreateCommand();cmd.CommandText="PRAGMA user_version";Assert.Equal(99L,cmd.ExecuteScalar());cmd.CommandText="PRAGMA user_version=1";cmd.ExecuteNonQuery();}
            Assert.DoesNotContain(test.Dav.Server.Requests,x=>x.Method=="GET");
        }
        finally{store.DeleteDatabase(root);}
    }
    [Fact(Timeout=30000)]
    public async Task ComHostStartsWithoutLoadingClrOrMainApplication()
    {
        using var process=Process.Start(Start("--shell-server"))!;
        try
        {
            await Task.Delay(1000);Assert.False(process.HasExited);process.Refresh();
            var modules=process.Modules.Cast<ProcessModule>().Select(x=>x.ModuleName).ToArray();Assert.Contains("HybridMount.Ext.dll",modules,StringComparer.OrdinalIgnoreCase);Assert.DoesNotContain(modules,x=>x.Equals("coreclr.dll",StringComparison.OrdinalIgnoreCase)||x.Equals("clr.dll",StringComparison.OrdinalIgnoreCase)||x.Equals("hostfxr.dll",StringComparison.OrdinalIgnoreCase));
        }
        finally{if(!process.HasExited){process.Kill();await process.WaitForExitAsync();}}
    }
    private static ProcessStartInfo Start(params string[] args)
    {
        var info=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"HybridMount.Native.exe")){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var arg in args)info.ArgumentList.Add(arg);return info;
    }
    private static async Task<JsonElement> Read(string path)
    {
        using var process=Process.Start(Start("--diagnose-thumbnail",path,"64"))!;var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));Assert.Equal(0,process.ExitCode);Assert.Empty(await error);using var result=JsonDocument.Parse(await output);return result.RootElement.Clone();
    }
    /// <summary>隔离缓存中的昂贵只读查询触发真实SQLite进度期限，区分超时与损坏；不放宽100ms预算或读取源。</summary>
    [Fact(Timeout=60000)]
    public async Task SqliteProgressDeadlineReturnsTimeoutWithoutSourceRead()
    {
        await using var test=await UploadFlowTests.Fixture.Create(false,legacyPreviews:false);var path=Path.Combine(test.Local,"文件.bin");var store=new ThumbnailCacheStore(ThumbnailCachePaths.ForMount(test.Id));var root=store.Configure(ThumbnailCacheReader.RootFor(test.Manager.Mounts.Single().Definition with{ThumbnailsEnabled=true}),true);
        string? schema=null;
        try
        {
            using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=store.Database,Pooling=false}.ToString())){await db.OpenAsync();using var command=db.CreateCommand();command.CommandText="SELECT sql FROM sqlite_master WHERE type='table' AND name='roots'";schema=(string)command.ExecuteScalar()!;command.CommandText="PRAGMA foreign_keys=OFF;DROP TABLE roots;CREATE VIEW roots AS WITH RECURSIVE n(x) AS (VALUES(0) UNION ALL SELECT x+1 FROM n WHERE x<1000000000) SELECT 'none' AS mount,'{}' AS json FROM n";await command.ExecuteNonQueryAsync();}
            var requests=test.Dav.Server.Requests.Count;var watch=Stopwatch.StartNew();var result=await Read(path);Assert.False(result.GetProperty("hit").GetBoolean());Assert.Equal(unchecked((int)0x800705b4),result.GetProperty("code").GetInt32());Assert.Equal("sqlite-timeout",result.GetProperty("stage").GetString());Assert.True(watch.Elapsed<TimeSpan.FromSeconds(3));Assert.Equal(requests,test.Dav.Server.Requests.Count);
        }
        finally
        {
            if(schema is not null)using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=store.Database,Pooling=false}.ToString())){await db.OpenAsync();using var command=db.CreateCommand();command.CommandText="DROP VIEW IF EXISTS roots;"+schema+";INSERT INTO roots(mount,json) VALUES($mount,$json)";command.Parameters.AddWithValue("$mount",test.Id.ToString("N"));command.Parameters.AddWithValue("$json",JsonSerializer.Serialize(root));await command.ExecuteNonQueryAsync();}
            store.DeleteDatabase(root);
        }
    }
}
