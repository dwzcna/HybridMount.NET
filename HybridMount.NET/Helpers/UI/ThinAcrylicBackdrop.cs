using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace HybridMount.Helpers.UI;

/// <summary>主窗口使用真正的桌面 Acrylic Thin，而非仅支持 Base 的内置 DesktopAcrylicBackdrop。</summary>
/// <remarks>默认配置由 WinUI 维护明暗主题、激活及高对比度；不覆写着色参数，保留系统实色回退。</remarks>
public sealed partial class ThinAcrylicBackdrop : SystemBackdrop
{
    private readonly Dictionary<ICompositionSupportsSystemBackdrop, DesktopAcrylicController> _controllers = [];

    /// <summary>每个目标持有独立控制器，窗口重建不共享已关闭的合成资源。</summary>
    /// <param name="connectedTarget">接入背景的窗口合成目标。</param>
    /// <param name="xamlRoot">由 WinUI 跟踪主题及输入状态的根节点。</param>
    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        var controller = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Thin };
        try
        {
            controller.SetSystemBackdropConfiguration(GetDefaultSystemBackdropConfiguration(connectedTarget, xamlRoot));
            controller.AddSystemBackdropTarget(connectedTarget);
            _controllers.Add(connectedTarget, controller);
        }
        catch
        {
            controller.Dispose();
            throw;
        }
    }

    /// <summary>背景撤销或窗口关闭时移除目标并释放控制器；隐藏但未销毁窗口仍保留原背景。</summary>
    /// <param name="disconnectedTarget">已经脱离背景的原窗口目标。</param>
    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        try
        {
            if (_controllers.Remove(disconnectedTarget, out var controller))
            {
                try { controller.RemoveSystemBackdropTarget(disconnectedTarget); }
                finally { controller.Dispose(); }
            }
        }
        finally { base.OnTargetDisconnected(disconnectedTarget); }
    }
}
