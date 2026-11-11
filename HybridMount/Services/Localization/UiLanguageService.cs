using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
namespace HybridMount.Services.Localization;

/// <summary>当前用户同部署作用域的唯一UiLanguage值，保存成功后才发布；无效持久值按system解释。</summary>
public interface IUiLanguageStore { string? Read(); void Write(string choice); }
public sealed record UiLanguageSelection(string Choice,string Effective)
{
    public bool IsSystem=>Choice=="system";
}
/// <summary>纯语言状态与资源服务；通知在锁外，调用者通过dispatcher交回UI。保存失败保留原快照，系统列表不受应用覆盖影响。</summary>
public sealed class UiLanguageService:IDisposable
{
    public static readonly IReadOnlyList<string> Choices=Array.AsReadOnly(new[]{"system","zh-CN","en-US","zh-HK"});
    private readonly object _gate=new();private readonly IUiLanguageStore _store;private readonly IUiTextProvider _texts;private readonly Func<IReadOnlyList<string>> _windows;
    private readonly Action<Action> _dispatch;private readonly Action<UiLanguageSelection>? _publish;
    private readonly ConcurrentDictionary<(string,string),string> _cache=new();private readonly HashSet<string> _missing=[];
    private UiLanguageSelection _current=new("system","zh-CN");private bool _loaded,_disposed;
    private volatile Dictionary<string,string>? _exact;private List<Pattern>? _patterns;
    public UiLanguageSelection Current=>Volatile.Read(ref _current);
    public bool IsLoaded {get{lock(_gate)return _loaded&&!_disposed;}}
    public ResourceMessage? Error {get;private set;}
    public int MissingResourceCount {get{lock(_missing)return _missing.Count;}}
    public event EventHandler? Changed;
    /// <summary>读取一次偏好；provider和store由应用／隔离测试提供，不保存Window或控件。</summary>
    public UiLanguageService(IUiLanguageStore store,IUiTextProvider texts,Func<IReadOnlyList<string>>? windows=null,Action<Action>? dispatch=null,Action<UiLanguageSelection>? publish=null)
    {
        _store=store;_texts=texts;_windows=windows??WindowsPreferredLanguages;_dispatch=dispatch??(action=>action());_publish=publish;
        try{var choice=Normalize(store.Read());_current=new(choice,choice=="system"?ResolveWindows():choice);_loaded=true;}catch(Exception error)when(StorageError(error)){Error=ResourceMessage.Create("Text_23F844F8F167");}
        PublishNative();
    }
    /// <summary>未知或不支持的持久值不回写，按system安全解释。</summary>
    public static string Normalize(string? value)=>Choices.FirstOrDefault(x=>string.Equals(x,value,StringComparison.OrdinalIgnoreCase))??"system";
    /// <summary>按真实Windows优先列表查找首个支持语言；Hans／简体到CN，Hant／港台澳到HK，英语到US，最终回退CN。</summary>
    public static string Resolve(IEnumerable<string> languages)
    {
        foreach(var original in languages){var value=original.Replace('_','-').ToLowerInvariant();if(value=="en"||value.StartsWith("en-",StringComparison.Ordinal))return "en-US";
            if(value=="zh"||value.StartsWith("zh-",StringComparison.Ordinal)){var parts=value.Split('-');return parts.Any(x=>x is "hant" or "tw" or "hk" or "mo" or "cht")?"zh-HK":"zh-CN";}}
        return "zh-CN";
    }
    private string ResolveWindows(){try{return Resolve(_windows());}catch(Exception error)when(error is InvalidOperationException or System.ComponentModel.Win32Exception){return "zh-CN";}}
    /// <summary>单值注册表写成功才推进语言。失败不发布、不修改当前UI；不设置CurrentCulture。</summary>
    public bool TrySave(string choice,out ResourceMessage? error)
    {
        UiLanguageSelection next;
        lock(_gate){if(_disposed||!_loaded){error=Error??ResourceMessage.Create("Text_989539954F09");return false;}
            if(!Choices.Contains(choice)){error=ResourceMessage.Create("Text_46B3DC4ED4A4");return false;}
            try{_store.Write(choice);}catch(Exception failure)when(StorageError(failure)){error=Error=ResourceMessage.Create("Text_8D3E89FEE69D");return false;}
            next=new(choice,choice=="system"?ResolveWindows():choice);Volatile.Write(ref _current,next);Error=null;}
        error=null;PublishNative();Notify();return true;
    }
    /// <summary>系统通知或激活时复核；显式语言和相同解析结果不产生更新，不重开InfoBar。</summary>
    public void ReevaluateSystemLanguage()
    {
        lock(_gate){if(_disposed||!_current.IsSystem)return;var language=ResolveWindows();if(language==_current.Effective)return;Volatile.Write(ref _current,new("system",language));}
        PublishNative();Notify();
    }
    private void PublishNative(){try{_publish?.Invoke(Current);}catch(Exception error){System.Diagnostics.Trace.TraceWarning("Language bridge: 0x{0:X8}",error.HResult);}}
    private void Notify()=>_dispatch(()=>{if(_disposed)return;if(Changed is {} handlers)foreach(EventHandler handler in handlers.GetInvocationList())try{handler(this,EventArgs.Empty);}catch(Exception error){System.Diagnostics.Trace.TraceWarning("Language observer: 0x{0:X8}",error.HResult);}});
    private static bool StorageError(Exception error)=>error is IOException or UnauthorizedAccessException or System.Security.SecurityException;
    /// <summary>PRI缺键／读取失败只记录键及数值错误，不包含路径、用户参数或凭据。</summary>
    internal string Template(string language,string key)=>_cache.GetOrAdd((language,key),pair=>
    {
        try{if(_texts.Read(pair.Item1,pair.Item2) is {Length:>0} value)return value;}catch(Exception error){System.Diagnostics.Trace.TraceWarning("Language resource {0}: 0x{1:X8}",pair.Item2,error.HResult);}
        lock(_missing)if(_missing.Add(pair.Item1+"/"+pair.Item2))System.Diagnostics.Trace.TraceWarning("Language resource missing: {0}/{1}",pair.Item1,pair.Item2);
        return UiCatalog.Templates.TryGetValue(pair.Item2,out var fallback)?fallback.Source:pair.Item2;
    });
    internal string Format(string key,object?[] args)
    {
        var values=args.Select(x=>x is ResourceMessage message?message.Render():x).ToArray();try{return string.Format(CultureInfo.CurrentCulture,Template(Current.Effective,key),values);}
        catch(FormatException){return string.Format(CultureInfo.CurrentCulture,UiCatalog.Templates[key].Source,values);}
    }
    private sealed record Pattern(string Key,Regex Regex,int[] Indices,int LiteralLength);
    /// <summary>旧业务反馈的兼容适配：仅匹配随包的完整固定模板，将其保存为键＋原始参数。未知原文保持Raw，不扫描或翻译参数正文。</summary>
    internal ResourceMessage Describe(string? value)
    {
        if(string.IsNullOrEmpty(value))return ResourceMessage.Raw(value);EnsurePatterns();if(_exact!.TryGetValue(value,out var key))return ResourceMessage.Create(key);
        if(value.Length>65536)return ResourceMessage.Raw(value);
        foreach(var pattern in _patterns!){var match=pattern.Regex.Match(value);if(!match.Success)continue;var count=pattern.Indices.Length==0?0:pattern.Indices.Max()+1;var args=new object?[count];var valid=true;
            for(var i=0;i<pattern.Indices.Length;i++){var index=pattern.Indices[i];var text=match.Groups["a"+i].Value;if(args[index] is {} previous&&previous.ToString()!=text){valid=false;break;}args[index]=text;}
            if(!valid)continue;var definition=UiCatalog.Templates[pattern.Key];foreach(var index in definition.LocalizableArguments)if(index<args.Length&&args[index] is string nested&&_exact.TryGetValue(nested,out var nestedKey))args[index]=ResourceMessage.Create(nestedKey);
            return ResourceMessage.Create(pattern.Key,args);}
        return ResourceMessage.Raw(value);
    }
    private void EnsurePatterns()
    {
        lock(_gate){if(_exact is not null)return;var exact=new Dictionary<string,string>(StringComparer.Ordinal);var patterns=new List<Pattern>();
            foreach(var (key,definition) in UiCatalog.Templates)foreach(var language in new[]{"zh-CN","en-US","zh-HK"}){
                var text=Template(language,key);var regex=new StringBuilder("\\A");var indices=new List<int>();var literal=0;
                for(var i=0;i<text.Length;){if(i+1<text.Length&&(text.AsSpan(i,2).SequenceEqual("{{")||text.AsSpan(i,2).SequenceEqual("}}"))){regex.Append(Regex.Escape(text[i].ToString()));literal++;i+=2;continue;}
                    if(text[i]=='{'&&text.IndexOf('}',i+1) is var end&&end>i){var part=text.Substring(i+1,end-i-1);var number=part.Split(',',':')[0];if(int.TryParse(number,NumberStyles.None,CultureInfo.InvariantCulture,out var index)){regex.Append("(?<a").Append(indices.Count).Append(">.*?)");indices.Add(index);i=end+1;continue;}}
                    regex.Append(Regex.Escape(text[i].ToString()));literal++;i++;}
                if(indices.Count==0)exact.TryAdd(text,key);else if(literal>=2){regex.Append("\\z");patterns.Add(new(key,new Regex(regex.ToString(),RegexOptions.Singleline|RegexOptions.CultureInvariant|RegexOptions.NonBacktracking,TimeSpan.FromMilliseconds(30)),indices.ToArray(),literal));}}
            _patterns=patterns.OrderByDescending(x=>x.LiteralLength).ToList();_exact=exact;}
    }
    /// <summary>GetUserPreferredUILanguages读取用户Windows优先列表，不使用已被PrimaryLanguageOverride影响的应用语言列表。</summary>
    public static IReadOnlyList<string> WindowsPreferredLanguages()
    {
        uint count=0,size=0;if(!GetUserPreferredUILanguages(8,ref count,null,ref size))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        if(size is 0 or >32768)return [];var buffer=new char[size];if(!GetUserPreferredUILanguages(8,ref count,buffer,ref size))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());return new string(buffer).Split('\0',StringSplitOptions.RemoveEmptyEntries);
    }
    public void Dispose(){lock(_gate){if(_disposed)return;_disposed=true;}Changed=null;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetUserPreferredUILanguages(uint flags,ref uint count,[Out]char[]? buffer,ref uint size);
}
/// <summary>同现有HKCU／打包隔离作用域保存REG_SZ，不改配置／备份／SQLite格式。</summary>
internal sealed class RegistryUiLanguageStore(string path):IUiLanguageStore
{
    public string? Read()=>WinRegisterService.ReadValue(path,"UiLanguage") as string;
    public void Write(string choice){using var key=Registry.CurrentUser.CreateSubKey(path,true)??throw new IOException("UiLanguage store unavailable");key.SetValue("UiLanguage",choice,RegistryValueKind.String);}
}
internal sealed class MemoryUiLanguageStore(string? choice="zh-CN"):IUiLanguageStore
{
    private string? _choice=choice;public string? Read()=>_choice;public void Write(string value)=>_choice=value;
}
