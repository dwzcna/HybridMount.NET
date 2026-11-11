using Microsoft.UI.Xaml;

namespace HybridMount.Helpers.UI;

/// <summary>供视图编译绑定使用的纯可见性规则；只折叠空反馈，不修改状态或拥有后台任务。</summary>
public static class TextVisibility
{
    /// <summary>无项目时显示空状态；不触发业务查询。</summary>
    public static Visibility ForEmptyCount(int count)=>count==0?Visibility.Visible:Visibility.Collapsed;
    /// <summary>加载完成且无项目时显示空状态。</summary>
    public static Visibility ForEmptyAndIdle(int count,bool reading)=>count==0&&!reading?Visibility.Visible:Visibility.Collapsed;
    /// <summary>布尔状态为真时显示；用于问题徽标及实际筛选状态。</summary>
    public static Visibility ForFlag(bool value)=>value?Visibility.Visible:Visibility.Collapsed;
    /// <summary>在 UI 线程求值；非空文字显示，空值或空字符串折叠，调用无副作用。</summary>
    /// <param name="text">原始状态或结果文字，不替换、截断或过滤内容。</param>
    /// <returns>文字区域的可见性；折叠后不占布局空间。</returns>
    public static Visibility ForText(string? text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>三个独立InfoBar全部关闭时折叠固定反馈行，不读取或清除正文。</summary>
    public static Visibility ForOpenFeedback(bool first,bool second,bool third)=>first||second||third?Visibility.Visible:Visibility.Collapsed;

    /// <summary>在 UI 线程合并测试、错误及文字反馈；所有来源为空时折叠整个反馈区域及其间距。</summary>
    /// <param name="probeOpen">连接测试反馈是否打开。</param>
    /// <param name="errorOpen">错误反馈是否打开。</param>
    /// <param name="status">其他操作结果文字。</param>
    /// <returns>有任一反馈时可见；无反馈时折叠，不改变反馈生命周期。</returns>
    public static Visibility ForFeedback(bool probeOpen, bool errorOpen, string? status) =>
        probeOpen || errorOpen || !string.IsNullOrEmpty(status) ? Visibility.Visible : Visibility.Collapsed;
}
