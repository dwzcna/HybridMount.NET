using System.Collections.ObjectModel;

namespace HybridMount.Models.Mounting;

/// <summary>按稳定身份更新 UI 快照集合；不清空列表，减少无关进度刷新引起的选择和滚动丢失。</summary>
internal static class SnapshotCollections
{
    /// <summary>由 UI 线程调用；保持目标顺序，只移动、替换或增删发生变化的条目。</summary>
    public static void Reconcile<T,TKey>(ObservableCollection<T> target,IReadOnlyList<T> source,Func<T,TKey> key) where TKey:notnull
    {
        var wanted=source.Select(key).ToHashSet();
        for(var i=target.Count-1;i>=0;i--)if(!wanted.Contains(key(target[i])))target.RemoveAt(i);
        for(var i=0;i<source.Count;i++)
        {
            if(i>=target.Count){target.Add(source[i]);continue;}
            if(!EqualityComparer<TKey>.Default.Equals(key(target[i]),key(source[i])))
            {
                var found=-1;for(var j=i+1;j<target.Count;j++)if(EqualityComparer<TKey>.Default.Equals(key(target[j]),key(source[i]))){found=j;break;}
                if(found>=0)target.Move(found,i);else target.Insert(i,source[i]);
            }
            if(!EqualityComparer<T>.Default.Equals(target[i],source[i]))target[i]=source[i];
        }
    }
}
