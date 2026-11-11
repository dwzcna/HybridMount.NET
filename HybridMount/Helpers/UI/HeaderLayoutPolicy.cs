namespace HybridMount.Helpers.UI;

/// <summary>页头空间决策；先展开前三项操作，再展开筛选文字，最后展开其余操作。</summary>
internal readonly record struct HeaderLayoutChoice(int Count,bool ShowLabels,double RequiredWidth);
internal static class HeaderLayoutPolicy
{
    /// <summary>仅计算布局，不改控件；扩展预留8DIP，收缩立即退回能放下的最高优先组合。</summary>
    internal static HeaderLayoutChoice Choose(double available,double compact,double expanded,IReadOnlyList<double> actions,HeaderLayoutChoice? previous=null,double menu=48,double gap=12)
    {
        var choices=new List<HeaderLayoutChoice>();var total=actions.Count;var first=Math.Min(3,total);
        double Required(int count,bool labels)=>(labels?expanded:compact)+(total>0?gap:0)+actions.Take(count).Sum()+(count<total?menu:0);
        for(var count=0;count<=first;count++)choices.Add(new(count,false,Required(count,false)));
        for(var count=first;count<=total;count++)choices.Add(new(count,true,Required(count,true)));
        var oldIndex=previous is {} old?choices.FindIndex(x=>x.Count==old.Count&&x.ShowLabels==old.ShowLabels):-1;
        var best=choices[0];
        for(var index=0;index<choices.Count;index++)
        {
            var extra=oldIndex>=0&&index>oldIndex?8:0;
            if(choices[index].RequiredWidth+extra<=Math.Max(0,available))best=choices[index];
        }
        return best;
    }
}
