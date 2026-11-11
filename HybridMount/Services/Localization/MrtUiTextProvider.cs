using System.Collections.Concurrent;
using Microsoft.Windows.ApplicationModel.Resources;
namespace HybridMount.Services.Localization;

/// <summary>从实际PRI按明确语言读取；属性登记与x:Uid共用PRI，不调用包专用API或改变线程区域格式。</summary>
internal sealed class MrtUiTextProvider:IUiTextProvider,IUiPropertyProvider
{
    private readonly ResourceManager _manager;
    private readonly ConcurrentDictionary<string,ResourceContext> _contexts=new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,IReadOnlyDictionary<string,string>> _properties=new(StringComparer.Ordinal);
    internal MrtUiTextProvider()
    {
        var directory=Path.GetDirectoryName(typeof(MrtUiTextProvider).Assembly.Location)!;
        var unpackaged=Path.Combine(directory,"HybridMount.pri");
        _manager=new ResourceManager(File.Exists(unpackaged)?unpackaged:Path.Combine(directory,"resources.pri"));
    }
    private ResourceContext Context(string language)=>_contexts.GetOrAdd(language,value=>
    {var result=_manager.CreateResourceContext();result.QualifierValues["Language"]=value;return result;});
    public string? Read(string language,string key)=>_manager.MainResourceMap.GetValue(key.StartsWith('/')?ResourceNames.ToPriPath(key).TrimStart('/'):"Strings/"+ResourceNames.ToPriPath(key),Context(language)).ValueAsString;
    /// <summary>枚举指定UID的字符串属性，只在第一次登记时读取；返回相对PRI资源名与应用属性别名。</summary>
    public IReadOnlyDictionary<string,string> PropertyResources(string id)=>_properties.GetOrAdd(id,value=>
    {
        var result=new Dictionary<string,string>(StringComparer.Ordinal);
        var resourceId=value.StartsWith("/Strings/",StringComparison.Ordinal)?value[9..]:value;
        var subtree=_manager.MainResourceMap.TryGetSubtree("Strings/"+resourceId);
        if(subtree is null)throw new InvalidOperationException("XAML resource ID missing: "+value);
        for(uint index=0;index<subtree.ResourceCount;index++)
        {
            var name=subtree.GetValueByIndex(index,Context("zh-CN")).Key;
            var prefix="Strings/"+resourceId+"/";
            if(name.StartsWith(prefix,StringComparison.Ordinal))name=name[prefix.Length..];
            else if(name.StartsWith(resourceId+"/",StringComparison.Ordinal))name=name[(resourceId.Length+1)..];
            var property=name.Contains("AutomationProperties/Name",StringComparison.Ordinal)?"AutomationName":
                name.Contains("AutomationProperties/HelpText",StringComparison.Ordinal)?"HelpText":
                name.Contains("ToolTipService/ToolTip",StringComparison.Ordinal)?"ToolTip":name;
            if(property.Contains('/'))throw new InvalidOperationException("XAML property unsupported: "+name);
            result.Add(property,"/Strings/"+resourceId+"/"+name);
        }
        return result;
    });
}
