using System.Collections;
using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using HybridMount.Services.Localization;
namespace HybridMount.Tests;

/// <summary>使用真实嵌入 RESX 和语言卫星，验证语言切换、标准回退和区域格式隔离；不访问正式偏好。</summary>
[Collection("Native IPC")]
public sealed class LocalizationTests
{
    [Theory]
    [InlineData("fr-FR,en-GB,zh-CN","en-US")][InlineData("zh-Hant-TW,en-US","zh-HK")]
    [InlineData("zh-MO","zh-HK")][InlineData("zh-Hans-SG","zh-CN")][InlineData("de-DE,fr-FR","zh-CN")]
    [InlineData("zh-HK,en-US","zh-HK")][InlineData("en-AU,zh-HK","en-US")][InlineData("zh-CN,en-US","zh-CN")]
    public void WindowsPriorityListUsesFirstSupportedFamily(string list,string language) => Assert.Equal(language,UILanguageService.Resolve(list.Split(',')));

    [Theory]
    [InlineData("zh-CN","关闭")][InlineData("en-US","Close")][InlineData("zh-HK","關閉")]
    public void ActualResourcesAndSatellitesAreRead(string language,string close)
    {
        Assert.Equal(close,UILanguageService.Messages.GetString("Close",CultureInfo.GetCultureInfo(language)));
        var values=Resources(language);
        Assert.True(values.Count>=880);
        Assert.All(values.Keys,key=>Assert.Matches("^[A-Za-z][A-Za-z0-9]*$",key));
        Assert.DoesNotContain(values.Keys,key=>key.StartsWith("Text_",StringComparison.Ordinal));
    }
    [Fact]
    public void ActualSatellitesHaveIdenticalKeysAndCompositeParameters()
    {
        var baseline=Resources("zh-CN");
        foreach(var language in new[]{"en-US","zh-HK"})
        {
            var values=Resources(language);Assert.Equal(baseline.Keys.Order(),values.Keys.Order());
            foreach(var (key,value) in values)
            {
                Assert.Equal(Parameters(baseline[key]),Parameters(value));
                _=System.Text.CompositeFormat.Parse(value);
            }
        }
    }
    [Fact]
    public void ResourceManagerUsesDeclaredNeutralFallbackAndNoGeneratedAccessor()
    {
        Assert.Equal("关闭",UILanguageService.Messages.GetString("Close",CultureInfo.GetCultureInfo("de-DE")));
        Assert.Null(UILanguageService.Messages.GetString("NoSuchSemanticResource",CultureInfo.GetCultureInfo("en-US")));
        var attribute=typeof(UILanguageService).Assembly.GetCustomAttributes(typeof(NeutralResourcesLanguageAttribute),false).Cast<NeutralResourcesLanguageAttribute>().Single();
        Assert.Equal("zh-CN",attribute.CultureName);
        Assert.DoesNotContain(typeof(UILanguageService).Assembly.GetTypes(),type=>type.Name=="Messages"&&type.Namespace=="HybridMount.Resources");
    }
    [Fact]
    public void InvalidStoredChoiceUsesSystemWithoutWritingAndExplicitChoiceIgnoresSystemChange()
    {
        var store=new Store("broken");var windows=new[]{"zh-Hant-TW"};
        using var service=new UILanguageService(store,windows:()=>windows);
        Assert.Equal(new("system","zh-HK"),service.Current);Assert.Equal(0,store.Writes);
        Assert.True(service.TrySave("en-US",out var error));Assert.Null(error);windows=["zh-CN"];
        service.ReevaluateSystemLanguage();Assert.Equal(new("en-US","en-US"),service.Current);Assert.Equal("en-US",store.Value);
    }
    [Fact]
    public void SaveFailureKeepsSnapshotAndDoesNotPublish()
    {
        var store=new Store("zh-CN");var published=new List<UILanguageSelection>();
        using var service=new UILanguageService(store,publish:published.Add);var count=published.Count;var changed=0;
        service.Changed+=(_,_)=>changed++;store.Fail=true;
        Assert.False(service.TrySave("zh-HK",out var error));Assert.NotNull(error);Assert.Equal(new("zh-CN","zh-CN"),service.Current);
        Assert.Equal(count,published.Count);Assert.Equal(0,changed);Assert.Equal("zh-CN",store.Value);
        store.Fail=false;Assert.True(service.TrySave("zh-HK",out _));Assert.Equal(1,changed);Assert.Equal(count+1,published.Count);
    }
    [Fact]
    public void ClosedMessageChangesLanguageWithoutReopeningAndExplicitPublishReopens()
    {
        using var service=new UILanguageService(new Store("zh-CN"));using var current=UILanguageService.Use(service);
        using var message=new PageStatusMessage();message.Show(ResourceMessage.Create("InterfaceLanguageSavedAndAppliedImmediately"),PageMessageSeverity.Success);message.IsOpen=false;
        Assert.True(service.TrySave("en-US",out _));
        Assert.Equal(UILanguageService.Messages.GetString("InterfaceLanguageSavedAndAppliedImmediately",CultureInfo.GetCultureInfo("en-US")),message.Text);
        Assert.False(message.IsOpen);Assert.Equal(PageMessageSeverity.Success,message.Severity);
        message.Show(ResourceMessage.Create("InterfaceLanguageSavedAndAppliedImmediately"),PageMessageSeverity.Success);Assert.True(message.IsOpen);
        message.Dispose();var text=message.Text;Assert.True(service.TrySave("zh-HK",out _));Assert.Equal(text,message.Text);
    }
    [Fact]
    public void NestedMessagesUseSameLanguageSnapshotAndNeverTranslateRawArguments()
    {
        var arguments=new object?[]{ResourceMessage.Create("Close"),"设置"};var message=ResourceMessage.Create("TwoLineDetails",arguments);arguments[1]="changed";
        Assert.Equal("Close\n设置",message.Render(CultureInfo.GetCultureInfo("en-US")).Replace("\r\n","\n"));
        Assert.Equal("關閉\n设置",message.Render(CultureInfo.GetCultureInfo("zh-HK")).Replace("\r\n","\n"));
        Assert.Equal("设置",ResourceMessage.Raw("设置").Render(CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal("server original / 原文",ResourceMessage.Raw("server original / 原文").Render());
    }
    [Theory]
    [InlineData("de-DE","en-US")][InlineData("en-US","zh-HK")][InlineData("zh-CN","en-US")]
    public async Task InterfaceLanguageDoesNotChangeRegionalFormatting(string region,string language)
    {
        var original=CultureInfo.CurrentCulture;var defaultRegion=CultureInfo.DefaultThreadCurrentCulture;
        try
        {
            CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo(region);
            using var service=new UILanguageService(new Store("zh-CN"));using var current=UILanguageService.Use(service);
            Assert.True(service.TrySave(language,out _));Assert.Equal(language,CultureInfo.CurrentUICulture.Name);Assert.Equal(language,CultureInfo.DefaultThreadCurrentUICulture?.Name);
            Assert.Equal(region,CultureInfo.CurrentCulture.Name);Assert.Same(defaultRegion,CultureInfo.DefaultThreadCurrentCulture);
            var snapshot=service.Current;
            var background=await Task.Run(()=>new{Text=UILanguageService.Messages.GetString("Close",snapshot.Culture),Number=(1234.5m).ToString("N1",CultureInfo.CurrentCulture)});
            Assert.Equal(UILanguageService.Messages.GetString("Close",snapshot.Culture),background.Text);
            Assert.Equal((1234.5m).ToString("N1",CultureInfo.GetCultureInfo(region)),background.Number);
        }
        finally{CultureInfo.CurrentCulture=original;}
    }
    [Fact]
    public void SystemRecheckChangesOnlyEffectiveLanguageWithoutRewritingChoice()
    {
        var store=new Store("system");var windows=new[]{"zh-CN"};using var service=new UILanguageService(store,windows:()=>windows);var changed=0;
        service.Changed+=(_,_)=>changed++;service.ReevaluateSystemLanguage();Assert.Equal(0,changed);windows=["en-CA"];service.ReevaluateSystemLanguage();
        Assert.Equal(new("system","en-US"),service.Current);Assert.Equal(1,changed);Assert.Equal(0,store.Writes);
    }
    [Fact]
    public void WeakDisplaySubscriptionDoesNotKeepReleasedOwnerAlive()
    {
        var observed=Observe();GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();Assert.False(observed.Target.IsAlive);
        using var service=new UILanguageService(new Store("zh-CN"));using var current=UILanguageService.Use(service);Assert.True(service.TrySave("en-US",out _));observed.Subscription.Dispose();
    }
    [Fact]
    public void PropertyNamesPreserveFrameworkNamespaceAndUseSeparateProvider()
    {
        var provider=new Properties();using var service=new UILanguageService(new Store("en-US"),provider);
        Assert.Equal("Greeting/[using:Microsoft.UI.Xaml.Automation]AutomationProperties/Name",ResourceNames.ToPriPath("Greeting.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name"));
        Assert.Equal("Greeting/Text",ResourceNames.ToPriPath("Greeting.Text"));
        Assert.Equal("en-US:/Strings/Greeting/Text",service.PropertyText("/Strings/Greeting/Text",CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal("Close",UILanguageService.Messages.GetString("Close",CultureInfo.GetCultureInfo("en-US")));
    }
    private static Dictionary<string,string> Resources(string language)=>UILanguageService.Messages.GetResourceSet(CultureInfo.GetCultureInfo(language),true,false)!.Cast<DictionaryEntry>().ToDictionary(entry=>(string)entry.Key,entry=>(string)entry.Value!);
    private static string[] Parameters(string text)=>Regex.Matches(text.Replace("{{","").Replace("}}",""),@"\{(\d+)(?:,[^}:]+)?(?::[^}]+)?\}").Select(match=>match.Groups[1].Value).Order().ToArray();
    [MethodImpl(MethodImplOptions.NoInlining)] private static (WeakReference Target,IDisposable Subscription) Observe(){var value=new object();return(new(value),UILanguageService.Observe(value,static _=>{}));}
    private sealed class Store(string? value):IUILanguageStore
    { public string? Value=value;public bool Fail;public int Writes;public string? Read()=>Value;public void Write(string choice){if(Fail)throw new IOException("injected");Writes++;Value=choice;} }
    private sealed class Properties:IXamlPropertyProvider
    { public string? Read(string language,string key)=>language+":"+key;public IReadOnlyDictionary<string,string> PropertyResources(string id)=>new Dictionary<string,string>{{"Text","/Strings/"+id+"/Text"}}; }
}
