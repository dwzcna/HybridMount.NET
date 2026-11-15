using HybridMount.UI.Binding;
using HybridMount.Commons.UI;
using HybridMount.Helpers.Threading;
using HybridMount.Models.Mounting;
using HybridMount.Services.Scheduling;
using HybridMount.UI;
using HybridMount.UI.Localization;
using HybridMount.UI.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
namespace HybridMount.Controls;

/// <summary>固定任务ID的实时只读详情。后台合并读取、UI原位更新；卸载后丢弃迟到结果，不取消业务任务。</summary>
public sealed class OperationDetailsView:UserControl,IDisposable
{
    private readonly TasksQueueService _queue;private readonly string _id;
    private readonly BackgroundSnapshotPump<(int Members,int Runs),OperationDetailsSnapshot?> _pump;
    private readonly TextBlock _summary=new(){TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true};
    private readonly TextBlock _empty=new(){TextWrapping=TextWrapping.Wrap};
    private readonly StackPanel _runs=new(){Spacing=12},_members=new(){Spacing=8};
    private readonly Dictionary<string,(Expander Expander,StackPanel Body)> _groups=new(StringComparer.Ordinal);
    private readonly Dictionary<(string,int),(FontIcon Icon,TextBlock Label,TextBlock Reason)> _steps=[];
    private readonly List<TextBlock> _results=[];
    private readonly Button _more=new(),_previous=new(),_older=new(),_newer=new();
    private readonly IDisposable _language;private OperationDetailsSnapshot? _snapshot;private int _offset,_runOffset;private bool _disposed;
    public OperationDetailsView(TasksQueueService queue,IUIDispatcher dispatcher,string id)
    {
        _queue=queue;_id=id;
        HorizontalAlignment=HorizontalAlignment.Stretch;HorizontalContentAlignment=HorizontalAlignment.Stretch;
        var body=new StackPanel{Spacing=12};body.Children.Add(_summary);body.Children.Add(_empty);body.Children.Add(_runs);var historyPaging=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};historyPaging.Children.Add(_newer);historyPaging.Children.Add(_older);body.Children.Add(historyPaging);body.Children.Add(_members);
        var paging=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};paging.Children.Add(_previous);paging.Children.Add(_more);body.Children.Add(paging);
        // 详情内容占满对话框可用宽度；横向受限后文字换行，保留唯一纵向滚动区域。
        Content=new ScrollViewer{Content=body,HorizontalContentAlignment=HorizontalAlignment.Stretch,
            HorizontalScrollMode=ScrollMode.Disabled,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        _pump=new(dispatcher,page=>queue.ReadOperationDetails(id,page.Members,page.Runs),Apply,error=>_summary.Text=error.Message);
        _more.Click+=(_,_)=>{_offset+=256;_pump.Request((_offset,_runOffset));};_previous.Click+=(_,_)=>{_offset=Math.Max(0,_offset-256);_pump.Request((_offset,_runOffset));};
        _older.Click+=(_,_)=>{_runOffset+=64;_pump.Request((_offset,_runOffset));};_newer.Click+=(_,_)=>{_runOffset=Math.Max(0,_runOffset-64);_pump.Request((_offset,_runOffset));};
        _language=UserInterface.ObserveLanguage(this,()=>{if(_snapshot is {} snapshot)Apply(snapshot);});
        queue.Changed+=Changed;Loaded+=LoadedView;_pump.Request((0,0));
    }
    private void LoadedView(object sender,RoutedEventArgs args)=>_pump.Request((_offset,_runOffset));
    private void Changed(){if(!_disposed)_pump.Request((_offset,_runOffset));}
    private void Apply(OperationDetailsSnapshot? value)
    {
        if(_disposed||value is null||value.Task.Id!=_id||(value.Offset!=_offset||value.RunOffset!=_runOffset))return;_snapshot=value;
        _summary.Text=value.Task.IssueDetails+"\n"+value.Task.ProgressText;
        _empty.Text=UILanguageService.Messages.GetString("OperationDetailsNoEvidence",UILanguageService.ResourceCulture)!;
        _empty.Visibility=value.Runs.Count==0?Visibility.Visible:Visibility.Collapsed;
        var visibleRuns=value.Runs.Select(run=>run.Id).ToHashSet(StringComparer.Ordinal);
        foreach(var id in _groups.Keys.Where(id=>!visibleRuns.Contains(id)).ToArray())
        {_runs.Children.Remove(_groups[id].Expander);_groups.Remove(id);foreach(var key in _steps.Keys.Where(key=>key.Item1==id).ToArray())_steps.Remove(key);}
        foreach(var run in value.Runs)
        {
            if(!_groups.TryGetValue(run.Id,out var group))
            {
                var panel=new StackPanel{Spacing=10};var expander=new Expander{Content=panel,IsExpanded=run.Id==value.Runs[0].Id,HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch};
                group=(expander,panel);_groups.Add(run.Id,group);
            }
            var desired=value.Runs.ToList().FindIndex(item=>item.Id==run.Id);var current=_runs.Children.IndexOf(group.Expander);
            if(current!=desired){if(current>=0)_runs.Children.RemoveAt(current);_runs.Children.Insert(desired,group.Expander);}
            if(group.Expander.Header is not TextBlock header)
            {header=new TextBlock{TextWrapping=TextWrapping.Wrap};group.Expander.Header=header;}
            header.Text=string.Format(System.Globalization.CultureInfo.CurrentCulture,UILanguageService.Messages.GetString("OperationDetailsAttempts",UILanguageService.ResourceCulture)!,run.Attempt,run.StartedUtc.ToLocalTime().ToString("g"));
            foreach(var step in run.Steps)
            {
                if(!_steps.TryGetValue((run.Id,step.Ordinal),out var row))
                {
                    var icon=new FontIcon{FontSize=18,VerticalAlignment=VerticalAlignment.Top};var label=new TextBlock{TextWrapping=TextWrapping.Wrap};var reason=new TextBlock{TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true};
                    var text=new StackPanel{Spacing=4};text.Children.Add(label);text.Children.Add(reason);var grid=new Grid{ColumnSpacing=10};grid.ColumnDefinitions.Add(new(){Width=GridLength.Auto});grid.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});grid.Children.Add(icon);Grid.SetColumn(text,1);grid.Children.Add(text);group.Body.Children.Add(grid);
                    row=(icon,label,reason);_steps[(run.Id,step.Ordinal)]=row;
                }
                row.Icon.Glyph=step.Glyph;row.Label.Text=step.DisplayName+" · "+step.DisplayState;
                AutomationProperties.SetName(row.Icon,step.DisplayState);row.Reason.Text=string.Join("\n",new[]{step.Reason,step.StartedUtc is {} started?string.Format(System.Globalization.CultureInfo.CurrentCulture,UILanguageService.Messages.GetString("OperationStepTiming",UILanguageService.ResourceCulture)!,started.ToLocalTime().ToString("g"),(step.FinishedUtc is {} finished?(finished-started).TotalSeconds:0).ToString("N2",System.Globalization.CultureInfo.CurrentCulture)):null,step.Total>0?TaskByteFormat.Format(step.Completed)+" / "+TaskByteFormat.Format(step.Total):null}.Where(text=>!string.IsNullOrEmpty(text)));row.Reason.Visibility=row.Reason.Text.Length==0?Visibility.Collapsed:Visibility.Visible;
            }
        }
        for(var i=0;i<value.Members.Count;i++)
        {
            if(i>=_results.Count){var text=new TextBlock{TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true};_results.Add(text);_members.Children.Add(text);}
            var result=value.Members[i];_results[i].Text=result.Path+" · "+result.DisplayStatus+(result.Message.Length>0?"\n"+result.Message:"");_results[i].Visibility=Visibility.Visible;
        }
        for(var i=value.Members.Count;i<_results.Count;i++)_results[i].Visibility=Visibility.Collapsed;
        _older.Content=UILanguageService.Messages.GetString("OperationDetailsOlder",UILanguageService.ResourceCulture);_older.Visibility=_runOffset+value.Runs.Count<value.RunCount?Visibility.Visible:Visibility.Collapsed;
        _newer.Content=UILanguageService.Messages.GetString("OperationDetailsNewer",UILanguageService.ResourceCulture);_newer.Visibility=_runOffset>0?Visibility.Visible:Visibility.Collapsed;
        _more.Content=UILanguageService.Messages.GetString("OperationDetailsLoadMore",UILanguageService.ResourceCulture);_more.Visibility=_offset+value.Members.Count<value.MemberCount?Visibility.Visible:Visibility.Collapsed;
        _previous.Content=UILanguageService.Messages.GetString("OperationDetailsPrevious",UILanguageService.ResourceCulture);_previous.Visibility=_offset>0?Visibility.Visible:Visibility.Collapsed;
        // 同ID内容原位更新，交由ScrollViewer锚定；不在每次进度刷新时重复请求相同滚动位置。
    }
    public void Dispose(){if(_disposed)return;_disposed=true;_queue.Changed-=Changed;Loaded-=LoadedView;_pump.Dispose();_language.Dispose();}
}
