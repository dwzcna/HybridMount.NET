using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HybridMount.Services.Localization;
namespace HybridMount.Tests;

/// <summary>注入纯资源和隔离偏好，验证Windows优先列表、失败回滚、消息键／原始参数及弱生命周期，不改正式语言设置。</summary>
[Collection("Native IPC")]
public sealed class LocalizationTests
{
    [Theory]
    [InlineData("fr-FR,en-GB,zh-CN","en-US")][InlineData("zh-Hant-TW,en-US","zh-HK")][InlineData("zh-MO","zh-HK")]
    [InlineData("zh-Hans-SG","zh-CN")][InlineData("de-DE,fr-FR","zh-CN")][InlineData("zh-HK,en-US","zh-HK")]
    [InlineData("en-AU,zh-HK","en-US")][InlineData("zh-CN,en-US","zh-CN")]
    public void WindowsPriorityListUsesFirstSupportedFamily(string list,string language)=>Assert.Equal(language,UiLanguageService.Resolve(list.Split(',')));
    [Fact]
    public void InvalidStoredChoiceUsesSystemWithoutWritingAndExplicitChoiceIgnoresSystemChange()
    {
        var store=new Store("broken");var windows=new[]{"zh-Hant-TW"};using var service=new UiLanguageService(store,new TextProvider(),()=>windows);
        Assert.Equal(new("system","zh-HK"),service.Current);Assert.Equal(0,store.Writes);Assert.True(service.TrySave("en-US",out var error));Assert.Null(error);windows=["zh-CN"];service.ReevaluateSystemLanguage();Assert.Equal(new("en-US","en-US"),service.Current);Assert.Equal("en-US",store.Value);
    }
    [Fact]
    public void SaveFailureKeepsSnapshotAndDoesNotPublishOrChangeRegionFormatting()
    {
        var store=new Store("zh-CN");var published=new List<UiLanguageSelection>();using var service=new UiLanguageService(store,new TextProvider(),()=>["en-US"],publish:published.Add);var count=published.Count;var culture=CultureInfo.CurrentCulture;var changed=0;service.Changed+=(_,_)=>changed++;store.Fail=true;
        Assert.False(service.TrySave("zh-HK",out var error));Assert.NotNull(error);Assert.Equal(new("zh-CN","zh-CN"),service.Current);Assert.Equal(count,published.Count);Assert.Equal(0,changed);Assert.Equal("zh-CN",store.Value);Assert.Same(culture,CultureInfo.CurrentCulture);
        store.Fail=false;Assert.True(service.TrySave("zh-HK",out _));Assert.Equal(1,changed);Assert.Equal(count+1,published.Count);Assert.Equal("zh-HK",service.Current.Effective);Assert.Same(culture,CultureInfo.CurrentCulture);
    }
    [Fact]
    public void ClosedMessageRendersNewLanguageWithoutReopeningAndIdenticalUserPublishReopens()
    {
        using var service=new UiLanguageService(new Store("zh-CN"),new TextProvider());using var current=UiText.Use(service);using var message=new PageStatusMessage();message.Show(ResourceMessage.Create(Key("界面语言已保存，立即生效。")),PageMessageSeverity.Success);message.IsOpen=false;
        Assert.True(service.TrySave("en-US",out _));Assert.Equal("Language saved and applied.",message.Text);Assert.False(message.IsOpen);Assert.Equal(PageMessageSeverity.Success,message.Severity);
        message.Show("Language saved and applied.",PageMessageSeverity.Success);Assert.True(message.IsOpen);message.Dispose();var saved=message.Text;Assert.True(service.TrySave("zh-HK",out _));Assert.Equal(saved,message.Text);Assert.True(message.IsOpen);
    }
    [Fact]
    public void OldTemplateBecomesKeyAndRawArgumentsAreNotTranslated()
    {
        using var service=new UiLanguageService(new Store("zh-CN"),new TextProvider());using var current=UiText.Use(service);var message=UiText.Describe("完整诊断 · 设置");Assert.Equal(Key("完整诊断 · {0}"),message.Key);Assert.Equal("设置",message.Arguments[0]);
        Assert.True(service.TrySave("en-US",out _));Assert.Equal("Full diagnostics · 设置",message.Render());Assert.Equal("设置",ResourceMessage.Raw("设置").Render());Assert.Equal("server original / 原文",ResourceMessage.Raw("server original / 原文").Render());
    }
    [Fact]
    public void LegacyPatternTimeoutDoesNotEscapeIntoBusinessPersistence()
    {
        var regex=new Regex("(a+)+$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(1));
        Assert.False(UiLanguageService.MatchLegacyPattern(regex,new string('a',100000)+"!").Success);
        using var service=new UiLanguageService(new Store("zh-CN"),new TextProvider());using var installed=UiText.Use(service);
        Assert.Equal("server original / 原文",UiText.Describe("server original / 原文").Render());
    }
    [Fact]
    public void SystemRecheckChangesOnlyEffectiveLanguageAndDoesNotRewriteChoice()
    {
        var store=new Store("system");var windows=new[]{"zh-CN"};using var service=new UiLanguageService(store,new TextProvider(),()=>windows);var changed=0;service.Changed+=(_,_)=>changed++;service.ReevaluateSystemLanguage();Assert.Equal(0,changed);windows=["en-CA"];service.ReevaluateSystemLanguage();Assert.Equal(new("system","en-US"),service.Current);Assert.Equal(1,changed);Assert.Equal(0,store.Writes);
    }
    [Fact]
    public void WeakDisplaySubscriptionDoesNotKeepReleasedOwnerAlive()
    {
        var value=Observe();GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();Assert.False(value.Target.IsAlive);using var service=new UiLanguageService(new Store("zh-CN"),new TextProvider());using var current=UiText.Use(service);Assert.True(service.TrySave("en-US",out _));value.Subscription.Dispose();
    }
    [Fact]
    public void SemanticPropertyResourceFormatsAndMalformedTranslationFallsBackWithoutCatalogEntry()
    {
        using var service=new UiLanguageService(new Store("en-US"),new SemanticProvider());using var installed=UiText.Use(service);
        Assert.Equal("默认 7",UiText.Text("Semantic.Status",7));
        Assert.Equal("Greeting/[using:Microsoft.UI.Xaml.Automation]AutomationProperties/Name",ResourceNames.ToPriPath("Greeting.[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name"));
        Assert.Equal("Greeting/Text",ResourceNames.ToPriPath("Greeting.Text"));
    }
    private sealed class SemanticProvider:IUiTextProvider {public string? Read(string language,string key)=>language=="en-US"?"broken {1}":"默认 {0}";}
    [MethodImpl(MethodImplOptions.NoInlining)]private static (WeakReference Target,IDisposable Subscription) Observe(){var value=new object();return(new(value),UiText.Observe(value,static _=>{}));}
    private static string Key(string source)=>"Text_"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..12];
    private sealed class Store(string? value):IUiLanguageStore
    {public string? Value=value;public bool Fail;public int Writes;public string? Read()=>Value;public void Write(string choice){if(Fail)throw new IOException("injected");Writes++;Value=choice;}}
    private sealed class TextProvider:IUiTextProvider
    {
        public string? Read(string language,string key)
        {
            if(key==Key("界面语言已保存，立即生效。"))return language switch{"en-US"=>"Language saved and applied.","zh-HK"=>"介面語言已儲存並即時套用。",_=>"界面语言已保存，立即生效。"};
            if(key==Key("完整诊断 · {0}"))return language switch{"en-US"=>"Full diagnostics · {0}","zh-HK"=>"完整診斷 · {0}",_=>"完整诊断 · {0}"};
            return UiCatalog.Templates.TryGetValue(key,out var item)?item.Source:null;
        }
    }
}
