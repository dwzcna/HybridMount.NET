using System.Globalization;
namespace HybridMount.Models.Mounting;

/// <summary>单个自定义状态的非秘密元数据；编号、文字和图标必须成组保留。</summary>
internal sealed record ShellCustomProperty(int Id,string Value,string Icon);
/// <summary>从Windows序列化属性容器读取的原始向量；允许诊断旧的空白残留，不将缺失项伪造为空状态。</summary>
internal sealed record ShellCustomState(IReadOnlyList<string> Ids,IReadOnlyList<string> Values,IReadOnlyList<string> Icons,bool HasUnknownFields=false,string? ProviderId=null)
{
    /// <summary>只替换或移除拥有的编号；空文字表示移除记录，不能留下编号而没有对应文字的向量。</summary>
    public (bool Changed,IReadOnlyList<ShellCustomProperty> Properties) Replace(int owned,string value,string icon)
    {
        var ids=Ids.Select(x=>int.TryParse(x,NumberStyles.None,CultureInfo.InvariantCulture,out var id)?id:throw new IOException("自定义状态编号无效，保留原属性。")).ToArray();
        var contains=ids.Contains(owned);if(!contains&&value.Length==0)return(false,[]);
        if(HasUnknownFields)throw new IOException("自定义状态包含未知字段，保留原属性。");
        var others=new List<ShellCustomProperty>();
        if(ids.Any(x=>x!=owned))
        {
            if(ids.Length!=Values.Count||ids.Length!=Icons.Count||ids.Distinct().Count()!=ids.Length)throw new IOException("其他自定义状态向量不完整，未覆盖原属性。");
            for(var i=0;i<ids.Length;i++)if(ids[i]!=owned)
            {
                if(string.IsNullOrEmpty(Values[i])||string.IsNullOrEmpty(Icons[i]))throw new IOException("其他自定义状态缺少文字或图标，未覆盖原属性。");
                others.Add(new(ids[i],Values[i],Icons[i]));
            }
        }
        if(value.Length>0)others.Add(new(owned,value,icon));
        var same=ids.Length==others.Count&&Values.Count==ids.Length&&Icons.Count==ids.Length&&
            others.Select((x,i)=>x.Id==ids[i]&&x.Value==Values[i]&&x.Icon==Icons[i]).All(x=>x);
        return(!same,others);
    }
}
