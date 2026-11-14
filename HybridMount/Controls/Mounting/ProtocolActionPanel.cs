using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace HybridMount.Controls.Mounting;

/// <summary>协议表单内按内容宽度排列操作按钮，空间不足时换行；只在UI布局线程工作，不修改按钮内容、事件或可见性。</summary>
public sealed class ProtocolActionPanel : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(nameof(Spacing), typeof(double),
        typeof(ProtocolActionPanel), new PropertyMetadata(8d, (owner, _) => ((ProtocolActionPanel)owner).InvalidateMeasure()));

    /// <summary>按钮及行间距；非法值按零处理，避免表单布局抛出异常。</summary>
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }
    private double Gap => double.IsFinite(Spacing) ? Math.Max(0, Spacing) : 0;

    /// <summary>用当前可用宽度测量子项，不给文本无限水平空间；返回全部可见按钮换行后的自然高度。</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = Math.Max(0, availableSize.Width);
        double line = 0, lineHeight = 0, height = 0, maximum = 0;
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            child.Measure(new Size(width, double.PositiveInfinity));
            var desired = child.DesiredSize;
            var item = Math.Min(width, desired.Width);
            if (line > 0 && line + Gap + item > width)
            {
                maximum = Math.Max(maximum, line); height += lineHeight + Gap; line = 0; lineHeight = 0;
            }
            line += (line > 0 ? Gap : 0) + item;
            lineHeight = Math.Max(lineHeight, desired.Height);
        }
        return new Size(Math.Max(maximum, line), height + lineHeight);
    }

    /// <summary>沿测量时的顺序排布；隐藏按钮不保留间隔，窄窗口不会裁掉后面的操作。</summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, lineHeight = 0;
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            var width = Math.Min(finalSize.Width, child.DesiredSize.Width);
            if (x > 0 && x + Gap + width > finalSize.Width) { x = 0; y += lineHeight + Gap; lineHeight = 0; }
            if (x > 0) x += Gap;
            child.Arrange(new Rect(x, y, width, child.DesiredSize.Height));
            x += width; lineHeight = Math.Max(lineHeight, child.DesiredSize.Height);
        }
        return finalSize;
    }
}
