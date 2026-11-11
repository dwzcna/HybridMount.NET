using System.Collections.Concurrent;
using Microsoft.Windows.ApplicationModel.Resources;
namespace HybridMount.Services.Localization;
/// <summary>从实际PRI按明确语言读取资源；上下文建立后只读，不改变线程或Windows区域格式。</summary>
internal sealed class MrtUiTextProvider:IUiTextProvider
{
    private readonly ResourceManager _manager;
    internal MrtUiTextProvider()
    {
        var directory=Path.GetDirectoryName(typeof(MrtUiTextProvider).Assembly.Location)!;
        // 非打包的索引名跟随程序集，MSIX使用合并后的resources.pri。
        var unpackaged=Path.Combine(directory,"HybridMount.pri");
        _manager=new ResourceManager(File.Exists(unpackaged)?unpackaged:Path.Combine(directory,"resources.pri"));
    }
    private readonly ConcurrentDictionary<string,ResourceContext> _contexts=new(StringComparer.Ordinal);
    public string? Read(string language,string key)
    {
        var context=_contexts.GetOrAdd(language,value=>{var result=_manager.CreateResourceContext();result.QualifierValues["Language"]=value;return result;});
        return _manager.MainResourceMap.GetValue("Resources/"+key,context).ValueAsString;
    }
}
