using HybridMount.Models.Diagnostics;
using HybridMount.Services;
using HybridMount.ViewModels;
namespace HybridMount.Tests;
/// <summary>隔离验证本地日期、夏令时、分页和导出快照，不访问现场日志或配置。</summary>
[Collection("Native IPC")]
public sealed class LogSingleDayTests
{
    [Theory]
    [InlineData(2026,3,8,23)][InlineData(2026,11,1,25)]
    public void DstDateUsesTwoLocalMidnights(int year,int month,int day,int hours)
    {var zone=TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");var range=LocalCalendarDay.Bounds(new DateTimeOffset(year,month,day,12,0,0,TimeSpan.FromHours(8)),zone);Assert.Equal(hours,(range.To!.Value-range.From!.Value).TotalHours);}
    [Fact]
    public void LocalDayIncludesStartAndExcludesNextMidnightAndNullHasNoRange()
    {
        var range=LocalCalendarDay.Bounds(new DateTimeOffset(2026,11,11,18,0,0,TimeSpan.FromHours(14)),TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"));
        Assert.Equal(new DateTimeOffset(2026,11,10,16,0,0,TimeSpan.Zero),range.From);
        var query=new DiagnosticQuery(FromUtc:range.From,ToUtc:range.To);
        DiagnosticEntry Entry(DateTimeOffset stamp)=>new(1,"test",stamp,"Information","source","body");
        Assert.True(query.Matches(Entry(range.From!.Value)));Assert.False(query.Matches(Entry(range.From.Value.AddTicks(-1))));
        Assert.True(query.Matches(Entry(range.To!.Value.AddTicks(-1))));Assert.False(query.Matches(Entry(range.To.Value)));
        Assert.Equal((null,null),LocalCalendarDay.Bounds(null,TimeZoneInfo.Utc));
    }
    [Fact]
    public async Task DateClearPreservesSeveritySearchAndPagedExportUsesSameSnapshot()
    {
        var directory=Path.Combine(Path.GetTempPath(),"HybridMount.SingleDay."+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            await using var logger=new LoggerService(Path.Combine(directory,"logs.db"));using var vm=new LogsViewModel(logger,new InlineDispatcher());
            for(var index=0;index<135;index++)logger.Queue("test","Warning","same day "+index);
            logger.Queue("test","Error","other severity");vm.Query="same day";vm.SelectedSeverityIndex=2;vm.SelectedDate=DateTimeOffset.Now;
            await vm.RefreshAsync();Assert.Equal(100,vm.Entries.Count);Assert.True(vm.HasSelectedDate);Assert.True(vm.CanLoadMore);
            var snapshot=vm.CaptureExportQuery();await vm.LoadOlderAsync();Assert.Equal(135,vm.Entries.Count);
            var export=await vm.ExportAsync(snapshot);Assert.Equal(135,export.Count);
            vm.SelectedDate=DateTimeOffset.Now.AddDays(-1);vm.SelectedDate=null;await vm.RefreshAsync();
            Assert.Equal("Warning",vm.Severity);Assert.Equal("same day",vm.Query);Assert.False(vm.HasSelectedDate);Assert.Null(vm.CaptureExportQuery().FromUtc);Assert.NotNull(snapshot.FromUtc);
            Assert.Equal(100,vm.Entries.Count);Assert.Equal(135,(await vm.ExportAsync(vm.CaptureExportQuery())).Count);
        }
        finally{Directory.Delete(directory,true);}
    }
    private sealed class InlineDispatcher:IUiDispatcher {public bool TryEnqueue(Action action){action();return true;}}
}
