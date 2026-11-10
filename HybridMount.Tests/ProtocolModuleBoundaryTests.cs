using System.Runtime.CompilerServices;
using HybridMount.Services.Mounting;

namespace HybridMount.Tests;

/// <summary>协议目录约束及公共工厂的资源边界；全部使用隔离仓库，不访问正式配置或外网。</summary>
public sealed class ProtocolModuleBoundaryTests
{
    private static string Root()
    {
        for(var folder=new DirectoryInfo(AppContext.BaseDirectory);folder is not null;folder=folder.Parent)
            if(File.Exists(Path.Combine(folder.FullName,"Directory.Build.props")))return folder.FullName;
        throw new IOException("目录边界检查需要源码仓库。");
    }
    [Fact]
    public void Protocol_implementations_and_wire_dependencies_stay_inside_modules()
    {
        var root=Path.Combine(Root(),"HybridMount");
        var dav=Path.Combine(root,"Models","Mounting","WebDAV")+Path.DirectorySeparatorChar;
        var s3=Path.Combine(root,"Models","Mounting","S3")+Path.DirectorySeparatorChar;
        var synology=Path.Combine(root,"Models","Mounting","Synology")+Path.DirectorySeparatorChar;
        foreach(var file in Directory.EnumerateFiles(root,"*.cs",SearchOption.AllDirectories).Where(x=>!x.Contains(Path.DirectorySeparatorChar+"obj"+Path.DirectorySeparatorChar)&&!x.Contains(Path.DirectorySeparatorChar+"bin"+Path.DirectorySeparatorChar)))
        {
            if(file.StartsWith(dav,StringComparison.OrdinalIgnoreCase)||file.StartsWith(s3,StringComparison.OrdinalIgnoreCase)||file.StartsWith(synology,StringComparison.OrdinalIgnoreCase))continue;
            var text=File.ReadAllText(file);
            foreach(var token in new[]{"using Amazon.","AmazonS3Client","AmazonSQSClient","using WebDAVClient","WebDAVClient.DavClient","HttpRequestMessage","IfMatch.ParseAdd","<d:sync-collection"})
                Assert.False(text.Contains(token,StringComparison.Ordinal),Path.GetRelativePath(root,file)+" 残留协议实现："+token);
        }
        Assert.True(File.Exists(Path.Combine(dav,"WebDavFileSource.cs")));Assert.True(File.Exists(Path.Combine(s3,"S3FileSource.cs")));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(root,"Services","Mounting"),"MountManager*.cs"));
        Assert.True(File.Exists(Path.Combine(root,"Commons","SourcePartialMoveException.cs")));
    }
    [Theory]
    [InlineData(true)][InlineData(false)]
    public async Task Factory_releases_source_when_context_or_probe_initialization_fails(bool contextFails)
    {
        await using var area=new DavFixture();var repo=new MountRepository(Path.Combine(area.Area,"boundary"));
        var source=new ControlledSource{ContextFails=contextFails,ProbeFails=!contextFails};var factory=new FileSourceFactory();factory.Register(FileSourceKind.Smb,(_,_,_)=>source);
        var mount=new MountDefinition(Guid.NewGuid(),"边界",area.Area,Source:new(FileSourceKind.Smb,"https://example.test/"));
        await Assert.ThrowsAsync<IOException>(()=>factory.CreateAsync(mount,repo,false,CancellationToken.None));
        Assert.Equal(1,source.AttachCalls);Assert.Equal(contextFails?0:1,source.ProbeCalls);Assert.Equal(1,source.DisposeCalls);
    }
    [Fact]
    public async Task Readonly_connection_test_does_not_attach_runtime_or_restore_and_disposes_once()
    {
        var source=new ControlledSource();var factory=new FileSourceFactory();factory.Register(FileSourceKind.Smb,(_,_,_)=>source);
        var result=await factory.TestConnectionAsync(new(FileSourceKind.Smb,"https://example.test/"),new());
        Assert.True(result.Success);Assert.Equal(0,source.AttachCalls);Assert.Equal(1,source.ProbeCalls);Assert.Equal(1,source.DisposeCalls);Assert.Equal(0,source.RecoveryCalls);
    }
    [Fact]
    public async Task Runtime_scope_and_public_compatibility_preserve_protocol_identity()
    {
        await using var dav=new DavFixture();await using var web=dav.Source();await using var s3=new S3Fixture();await using var source=s3.Source();
        Assert.Equal(web.RemoteScope(4),((IMountSourceRuntime)web).RemoteScope(4));Assert.NotEqual(web.RemoteScope(4),web.RemoteScope(5));
        Assert.Equal(source.RemoteScope(4),((IMountSourceRuntime)source).RemoteScope(4));Assert.NotEqual(source.RemoteScope(4),source.RemoteScope(5));
        Assert.Equal("HybridMount.Commons.SourcePartialMoveException",typeof(SourcePartialMoveException).FullName);
        Assert.Equal("HybridMount.Commons.IFileSource",typeof(IFileSource).FullName);
    }
    private sealed class ControlledSource : IFileSource,IMountSourceRuntime
    {
        public bool ContextFails,ProbeFails;public int AttachCalls,ProbeCalls,DisposeCalls,RecoveryCalls;
        public FileSourceCapabilities Capabilities {get;}=new(CapabilitySupport.Unknown,CapabilitySupport.Unknown,CapabilitySupport.Unknown,CapabilitySupport.Unknown,CapabilitySupport.Unknown,SourceCaseSensitivity.Unknown);
        public string RemoteScope(long revision)=>"scope";
        public void AttachRuntime(MountDefinition mount,MountRepository repository){AttachCalls++;if(ContextFails)throw new IOException("context");}
        public Task BeforeRegistrationAsync(CancellationToken token){RecoveryCalls++;return Task.CompletedTask;}
        public Task ProbeAsync(CancellationToken token=default){ProbeCalls++;return ProbeFails?Task.FromException(new IOException("probe")):Task.CompletedTask;}
        public ValueTask DisposeAsync(){DisposeCalls++;return ValueTask.CompletedTask;}
        public async IAsyncEnumerable<SourceEntry> ListAsync(string path,[EnumeratorCancellation]CancellationToken token=default){await Task.CompletedTask;yield break;}
        public Task<SourceEntry> GetMetadataAsync(string path,CancellationToken token=default)=>throw new NotSupportedException();
        public ValueTask<int> ReadRangeAsync(SourceEntry entry,long offset,Memory<byte> buffer,CancellationToken token=default)=>throw new NotSupportedException();
        public Task<SourceEntry> UploadAsync(string path,Stream content,string? expectedVersion,CancellationToken token=default)=>throw new NotSupportedException();
        public Task<SourceEntry> CreateDirectoryAsync(string path,CancellationToken token=default)=>throw new NotSupportedException();
        public Task<SourceEntry> MoveAsync(string path,string destination,string expectedVersion,CancellationToken token=default)=>throw new NotSupportedException();
        public Task DeleteAsync(string path,string expectedVersion,CancellationToken token=default)=>throw new NotSupportedException();
    }
}
