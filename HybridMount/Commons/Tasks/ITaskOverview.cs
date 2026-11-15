using HybridMount.Models.Tasks;
namespace HybridMount.Commons.Tasks;

/// <summary>进程内概览只读边界；Changed可来自任意线程，界面自行合并派发，不能控制任务执行。</summary>
public interface ITaskOverview
{
    TaskOverviewSnapshot Current { get; }
    event Action? Changed;
}
