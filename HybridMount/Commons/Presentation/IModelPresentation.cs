using System.Globalization;
namespace HybridMount.Commons.Presentation;

/// <summary>兼容现有模型派生字符串的纯展示边界；实现不得访问控件或发起业务操作。</summary>
public interface IModelPresentation
{
    string Render(object model,string member,CultureInfo? culture=null);
}
/// <summary>应用寿命内只装配一次，不拥有窗口／Dispatcher；未装配或未知成员明确失败。</summary>
public static class ModelPresentation
{
    private static IModelPresentation? _renderer;
    public static void Configure(IModelPresentation renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        if(Interlocked.CompareExchange(ref _renderer,renderer,null) is not null)
            throw new InvalidOperationException("模型展示已经装配。");
    }
    public static string Render(object model,string member,CultureInfo? culture=null) =>
        (Volatile.Read(ref _renderer)??throw new InvalidOperationException("模型展示尚未装配。")).Render(model,member,culture);
}
