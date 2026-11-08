using System.Xml.Linq;
using HybridMount.Services.Mounting;
namespace HybridMount.Tests;

/// <summary>缩略图注册按同一应用完整绑定核验；模拟仅验证清单边界，不代替Explorer端到端测试。</summary>
public sealed class ShellThumbnailCapabilityTests
{
    private const string Id="A65802EB-4D1C-4C7E-A519-34C76B9BB281";
    private static XDocument Manifest(string path="HybridMount.NET.exe",bool bound=true)=>XDocument.Parse($"""
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" xmlns:d="http://schemas.microsoft.com/appx/manifest/desktop/windows10/3" xmlns:c="http://schemas.microsoft.com/appx/manifest/com/windows10">
        <Applications><Application Id="App" Executable="HybridMount.NET.exe"><Extensions>
        <d:Extension Category="windows.cloudFiles"><d:CloudFiles><d:ThumbnailProviderHandler {(bound?$"Clsid='{Id}'":"")}/></d:CloudFiles></d:Extension>
        <c:Extension Category="windows.comServer"><c:ComServer><c:ExeServer Executable="{path}" Arguments="--thumbnail-server"><c:Class Id="{Id}"/></c:ExeServer></c:ComServer></c:Extension>
        </Extensions></Application></Applications></Package>
        """);
    [Fact(Timeout=120000)]
    public async Task Root_diagnostics_do_not_hydrate_repin_reidentify_or_access_remote_content()
    {
        await using var fixture=await UploadFlowTests.Fixture.Create(false);
        var mount=fixture.Manager.Mounts.Single().Definition;var path=Path.Combine(fixture.Local,"文件.bin");var identity=MountPathGuard.FileIdentity(path);var root=CloudRootPolicy.Read(mount.LocalPath);
        var before=CloudFilesNative.Inspect(path)!.Value;var requests=fixture.Dav.Server.Requests.Count;
        var description=CloudShellState.RootDescription(mount);Assert.Contains("原生水合策略：",description);Assert.Contains("根Shell应用：",description);Assert.Contains("缩略图运行能力：",description);
        var after=CloudFilesNative.Inspect(path)!.Value;Assert.Equal(identity,MountPathGuard.FileIdentity(path));Assert.Equal(before.Info.PinState,after.Info.PinState);Assert.Equal(before.Info.InSyncState,after.Info.InSyncState);Assert.Equal(before.Info.OnDiskSize,after.Info.OnDiskSize);Assert.Equal(before.Identity,after.Identity);
        var final=CloudRootPolicy.Read(mount.LocalPath);Assert.Equal(root.Hydration,final.Hydration);Assert.Equal(root.FileId,final.FileId);Assert.Equal(root.Identity,final.Identity);Assert.Equal(requests,fixture.Dav.Server.Requests.Count);Assert.False(CloudFilesNative.HasFullContent(path,new FileInfo(path).Length));
    }
    [Fact]
    public void Binding_requires_cloud_entry_same_application_class_and_existing_payload()
    {
        var document=Manifest();Assert.True(CloudShellState.HasThumbnailBinding(document,path=>path is "HybridMount.Ext.dll" or "HybridMount.NET.exe"));
        Assert.False(CloudShellState.HasThumbnailBinding(document,_=>false));Assert.False(CloudShellState.HasThumbnailBinding(Manifest(bound:false),_=>true));
        XNamespace f=document.Root!.Name.Namespace,c="http://schemas.microsoft.com/appx/manifest/com/windows10";
        var server=document.Descendants(c+"Extension").Single();server.Remove();document.Root.Element(f+"Applications")!.Add(new XElement(f+"Application",new XAttribute("Id","Other"),new XElement(f+"Extensions",server)));
        Assert.False(CloudShellState.HasThumbnailBinding(document,_=>true));
    }
    [Theory]
    [InlineData("../HybridMount.Ext.dll")]
    [InlineData("C:\\HybridMount.Ext.dll")]
    [InlineData("folder//HybridMount.Ext.dll")]
    [InlineData("HybridMount.Ext.dll:stream")]
    public void Invalid_com_payload_path_never_reaches_file_probe(string path)
    {
        var probes=0;Assert.False(CloudShellState.HasThumbnailBinding(Manifest(path),_=>{probes++;return true;}));Assert.Equal(0,probes);
    }
}
