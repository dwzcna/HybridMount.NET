using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace HybridMount.UI;

/// <summary>共享操作组按原顺序换行；对齐由使用位置指定，测量及排列只在UI线程执行。</summary>
public sealed class ActionPanel : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(ActionPanel), new PropertyMetadata(12d, (sender, _) => ((ActionPanel)sender).InvalidateMeasure()));

    public static readonly DependencyProperty ContentAlignmentProperty = DependencyProperty.Register(
        nameof(ContentAlignment), typeof(HorizontalAlignment), typeof(ActionPanel), new PropertyMetadata(HorizontalAlignment.Left,
            (sender, _) => ((ActionPanel)sender).InvalidateArrange()));
    /// <summary>每行使用Left、Center或Right对齐；不改变按钮样式或命令。</summary>
    public HorizontalAlignment ContentAlignment { get => (HorizontalAlignment)GetValue(ContentAlignmentProperty); set => SetValue(ContentAlignmentProperty, value); }

    /// <summary>同一行和相邻行的间距；不参与按钮背景、命令或可用状态处理。</summary>
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    /// <summary>先测自然宽度，单个长按钮超出视口时再约束宽度，让按钮正文换行而非裁剪。</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (!double.IsPositiveInfinity(availableSize.Width) && child.DesiredSize.Width > availableSize.Width)
                child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        }
        var rows = Rows(availableSize.Width);
        return new Size(rows.Count == 0 ? 0 : rows.Max(row => row.Width), rows.Sum(row => row.Height) + Math.Max(0, rows.Count - 1) * Gap);
    }

    /// <summary>每行保持声明顺序并按使用位置对齐；仅排列可见项，不拦截点击、焦点或键盘事件。</summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var y = 0d;
        foreach (var row in Rows(finalSize.Width))
        {
            var remaining = Math.Max(0, finalSize.Width - row.Width);
            var x = ContentAlignment == HorizontalAlignment.Right ? remaining : ContentAlignment == HorizontalAlignment.Center ? remaining / 2 : 0;
            foreach (var child in row.Items)
            {
                var width = Math.Min(finalSize.Width, child.DesiredSize.Width);
                child.Arrange(new Rect(x, y, width, row.Height));
                x += width + Gap;
            }
            y += row.Height + Gap;
        }
        return finalSize;
    }

    private double Gap => double.IsFinite(Spacing) ? Math.Max(0, Spacing) : 0;

    /// <summary>用已有测量结果计算行，隐藏项不留下空位；不读取应用或系统状态。</summary>
    private List<Row> Rows(double width)
    {
        var rows = new List<Row>();
        foreach (var child in Children.Where(child => child.Visibility == Visibility.Visible))
        {
            var itemWidth = Math.Min(width, child.DesiredSize.Width);
            if (rows.Count == 0 || rows[^1].Width + Gap + itemWidth > width)
                rows.Add(new Row());
            var row = rows[^1];
            if (row.Items.Count > 0) row.Width += Gap;
            row.Items.Add(child);
            row.Width += itemWidth;
            row.Height = Math.Max(row.Height, child.DesiredSize.Height);
        }
        return rows;
    }

    private sealed class Row
    {
        internal List<UIElement> Items { get; } = [];
        internal double Width { get; set; }
        internal double Height { get; set; }
    }
}
