using System.ComponentModel;
using System.Runtime.InteropServices;
using HybridMount.Models.Mounting.Windows;
using HybridMount.Services.Localization;
namespace HybridMount.Tests;

/// <summary>以纯委托和隔离清单核验包身份分支，不修改系统语言、包注册或正式挂载。</summary>
public sealed class UnpackagedInitializationTests
{
    /// <summary>无包不调用包专用语言API；两种模式均发布原始语言快照。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_language_summary_is_published_in_both_modes(bool packaged)
    {
        var selection=new UiLanguageSelection("en-US","en-US");var calls=new List<string>();var failures=new List<Exception>();
        UiLanguagePublication.Publish(selection,()=>packaged,language=>calls.Add("override:"+language),
            value=>{Assert.Same(selection,value);calls.Add("native");},(_,error)=>failures.Add(error));
        Assert.Equal(packaged?new[]{"override:en-US","native"}:new[]{"native"},calls);Assert.Empty(failures);
    }
    /// <summary>包专用语言覆盖失败后仍发布摘要，并保留固定阶段和原错误。</summary>
    [Fact]
    public void Override_failure_does_not_block_native_summary()
    {
        var selection=new UiLanguageSelection("system","zh-HK");var failure=new COMException("injected",unchecked((int)0x80073D54));
        UiLanguageSelection? published=null;var errors=new List<(string Stage,Exception Error)>();
        UiLanguagePublication.Publish(selection,()=>true,_=>throw failure,value=>published=value,(stage,error)=>errors.Add((stage,error)));
        Assert.Same(selection,published);var captured=Assert.Single(errors);Assert.Equal("Language override",captured.Stage);Assert.Same(failure,captured.Error);
    }
    /// <summary>身份未知保留原诊断，不访问包API；原生摘要仍独立尝试。</summary>
    [Fact]
    public void Unknown_identity_error_is_reported_without_calling_package_api()
    {
        var failure=new Win32Exception(5);var overridden=false;var published=false;var errors=new List<(string Stage,Exception Error)>();
        UiLanguagePublication.Publish(new("zh-CN","zh-CN"),()=>throw failure,_=>overridden=true,_=>published=true,(stage,error)=>errors.Add((stage,error)));
        Assert.False(overridden);Assert.True(published);var captured=Assert.Single(errors);Assert.Equal("Language package identity",captured.Stage);Assert.Same(failure,captured.Error);
    }
    /// <summary>两侧同时失败时分别保留阶段及原异常；不把摘要错误混为包身份故障。</summary>
    [Fact]
    public void Both_language_failures_are_reported_separately()
    {
        var primary=new InvalidOperationException("override");var native=new IOException("summary");var errors=new List<(string Stage,Exception Error)>();
        UiLanguagePublication.Publish(new("en-US","en-US"),()=>true,_=>throw primary,_=>throw native,(stage,error)=>errors.Add((stage,error)));
        Assert.Equal(new[]{"Language override","Native language summary"},errors.Select(item=>item.Stage));
        Assert.Same(primary,errors[0].Error);Assert.Same(native,errors[1].Error);
    }
    /// <summary>无包Shell探测直接关闭自有扩展，不能通过访问Package.Current触发异常。</summary>
    [Fact]
    public void Unpackaged_shell_policy_does_not_query_package_location()
    {
        var result=ShellExtensionPolicy.ReadRegistration(()=>false,()=>throw new InvalidOperationException("package API must not run"));
        Assert.Equal((false,false,false),result);
    }
    /// <summary>未知身份与已确认有包后的API错误均原样传播，不能返回无包结果。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shell_identity_or_location_errors_are_not_treated_as_unpacked(bool identitySucceeded)
    {
        var failure=new Win32Exception(5);var locationRead=false;
        var caught=Assert.Throws<Win32Exception>(()=>ShellExtensionPolicy.ReadRegistration(
            ()=>identitySucceeded?true:throw failure,()=>{locationRead=true;throw failure;}));
        Assert.Same(failure,caught);Assert.Equal(identitySucceeded,locationRead);
    }
    /// <summary>有包时读取当前提供的清单；缺失或损坏清单仍保留有包状态并关闭自有扩展。</summary>
    [Theory]
    [InlineData("valid")]
    [InlineData("missing")]
    [InlineData("invalid")]
    public void Packaged_shell_policy_reads_manifest_without_losing_identity(string manifest)
    {
        var directory=Path.Combine(Path.GetTempPath(),"HybridMount.IdentityTests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            if(manifest!="missing")File.WriteAllText(Path.Combine(directory,"AppxManifest.xml"),manifest=="valid"?
                "<Package xmlns='http://schemas.microsoft.com/appx/manifest/foundation/windows10'><Applications><Application Id='App'/></Applications></Package>":"<invalid");
            var calls=0;var result=ShellExtensionPolicy.ReadRegistration(()=>true,()=>{calls++;return directory;});
            Assert.Equal((true,false,false),result);Assert.Equal(1,calls);
        }
        finally{Directory.Delete(directory,true);}
    }
}
