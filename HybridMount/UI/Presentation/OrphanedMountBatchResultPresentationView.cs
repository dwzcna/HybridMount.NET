using HybridMount.UI.Localization;
using HybridMount.Models.Mounting;
namespace HybridMount.UI.Presentation;

/// <summary>只格式化不可变模型；后台调用不访问控件、Dispatcher、系统目录或网络。</summary>
internal sealed class OrphanedMountBatchResultPresentationView(OrphanedMountBatchResult value)
{
    private readonly OrphanedMountBatchResult _value=value;
    private IReadOnlyList<OrphanedMountItemResult> Items => _value.Items;
    private int Completed => _value.Completed;
    private int Failed => _value.Failed;
    private int Canceled => _value.Canceled;
    private int NotStarted => _value.NotStarted;
public string Summary=>string.Format(System.Globalization.CultureInfo.CurrentCulture, global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("SucceededFailedCanceledNotStarted", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)!, Completed, Failed, Canceled, NotStarted);
/// <summary>生成用户可复制的阶段及错误清单；成功项也保留路径。</summary>
    public string Detail=>Summary+"\n\n"+string.Join("\n\n",Items.Select(x=>x.Root.LocalPath+"\n"+x.State+" · "+x.Stage+
        (x.HResult is {} code?$" · 0x{code:X8}":"")+(x.Errors.Count>0?"\n"+string.Join("\n",x.Errors):"")));
}
