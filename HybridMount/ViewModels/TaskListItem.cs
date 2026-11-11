using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HybridMount.Models.Mounting;
namespace HybridMount.ViewModels;

/// <summary>任务ID对应的常驻展示行；UI线程更新原对象，只通知实际变化的展示属性，不替换列表容器或重启动画。</summary>
public sealed class TaskListItem(MountOperationSnapshot snapshot):ObservableObject
{
    private MountOperationSnapshot _snapshot=snapshot;
    public MountOperationSnapshot Snapshot=>_snapshot;
    public string Id=>_snapshot.Id;
    public string State=>_snapshot.State;
    public string Operation=>_snapshot.Operation;
    public string? Error=>_snapshot.Error;
    public MountingPriority? Priority=>_snapshot.Priority;
    public string? WaitingTaskId=>_snapshot.WaitingTaskId;
    public string MountName=>_snapshot.MountName;
    public string CardOperation=>_snapshot.CardOperation;
    public string CardSchedule=>_snapshot.CardSchedule;
    public string CardState=>_snapshot.CardState;
    public string CardPath=>_snapshot.CardPath;
    public string ProgressDetail=>_snapshot.ProgressDetail;
    public string IssueDetails=>_snapshot.IssueDetails;
    public double ProgressPercent=>_snapshot.ProgressPercent;
    public TaskIssueKind IssueKind=>_snapshot.IssueKind;
    public long BytesTransferred=>_snapshot.BytesTransferred;
    public long TotalBytes=>_snapshot.TotalBytes;
    private object?[] Values()=>[State,Operation,Error,MountName,CardOperation,CardSchedule,CardState,CardPath,ProgressDetail,IssueDetails,ProgressPercent,IssueKind,BytesTransferred,TotalBytes];
    private static readonly string[] Names=[nameof(State),nameof(Operation),nameof(Error),nameof(MountName),nameof(CardOperation),nameof(CardSchedule),nameof(CardState),nameof(CardPath),nameof(ProgressDetail),nameof(IssueDetails),nameof(ProgressPercent),nameof(IssueKind),nameof(BytesTransferred),nameof(TotalBytes)];
    /// <summary>完整快照仍由应用服务拥有；同文Results的新列表不制造刷新。ID不符拒绝调用，不修改现有行。</summary>
    internal void Update(MountOperationSnapshot snapshot)
    {
        if(snapshot.Id!=Id)throw new ArgumentException("Task row identity changed",nameof(snapshot));
        if(snapshot with{Results=_snapshot.Results}==_snapshot&&snapshot.Results.SequenceEqual(_snapshot.Results))return;
        var previous=Values();_snapshot=snapshot;var next=Values();for(var i=0;i<Names.Length;i++)if(!Equals(previous[i],next[i]))OnPropertyChanged(Names[i]);
    }
    /// <summary>只对真实增删／排序使用集合事件，同ID进度更新仅在原行发布；返回结构是否改变。</summary>
    internal static bool Reconcile(ObservableCollection<TaskListItem> rows,IReadOnlyList<MountOperationSnapshot> snapshots)
    {
        var changed=!rows.Select(row=>row.Id).SequenceEqual(snapshots.Select(value=>value.Id),StringComparer.Ordinal);
        var existing=rows.ToDictionary(row=>row.Id,StringComparer.Ordinal);var wanted=snapshots.Select(value=>value.Id).ToHashSet(StringComparer.Ordinal);
        for(var i=rows.Count-1;i>=0;i--)if(!wanted.Contains(rows[i].Id))rows.RemoveAt(i);
        for(var index=0;index<snapshots.Count;index++)
        {
            var snapshot=snapshots[index];
            if(existing.TryGetValue(snapshot.Id,out var row)){row.Update(snapshot);if(index>=rows.Count||!ReferenceEquals(rows[index],row)){var current=rows.IndexOf(row);rows.Move(current,index);}}
            else rows.Insert(index,new(snapshot));
        }
        return changed;
    }
}
