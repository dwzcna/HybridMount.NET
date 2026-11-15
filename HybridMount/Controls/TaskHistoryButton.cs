using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace HybridMount.Controls;

/// <summary>沿用原生 AppBarButton 模板和状态，在其图标展示器内叠加字体图标；Icon 属性仍为合法 IconElement。</summary>
public sealed class TaskHistoryButton : AppBarButton
{
    public static readonly DependencyProperty BadgeGlyphProperty = DependencyProperty.Register(
        nameof(BadgeGlyph), typeof(string), typeof(TaskHistoryButton), new PropertyMetadata("", (target, _) => ((TaskHistoryButton)target).UpdateIcon()));
    public string BadgeGlyph { get => (string)GetValue(BadgeGlyphProperty); set => SetValue(BadgeGlyphProperty, value); }
    public TaskHistoryButton()
    {
        DefaultStyleKey = typeof(AppBarButton);
        // 原生溢出区的隐式AppBarButton样式不覆盖派生类；解除默认68DIP固定宽，保留原生文本和图标共同测量。
        Width = double.NaN;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Icon = new FontIcon { Glyph = "\uE74D" };
    }
    /// <summary>溢出区和主区使用同一原生模板；重新应用时重新取得展示器，不缓存已离开的视觉节点。</summary>
    protected override void OnApplyTemplate() { base.OnApplyTemplate(); UpdateIcon(); }
    private void UpdateIcon()
    {
        if (GetTemplateChild("Content") is not ContentPresenter presenter) return;
        var size = (double)Application.Current.Resources["ActionIconSize"];
        var grid = new Grid { Width = size, Height = size, IsHitTestVisible = false };
        var baseIcon = new FontIcon { Glyph = "\uE74D", FontSize = size };
        var badge = new FontIcon { Glyph = BadgeGlyph, FontSize = size * .55, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
        // 颜色由原生按钮状态更新；高对比度、禁用和指针悬停不使用固定色或位图。
        foreach (var icon in new[] { baseIcon, badge })
        {
            icon.SetBinding(IconElement.ForegroundProperty, new Binding { Source = presenter, Path = new PropertyPath("Foreground"), Mode = BindingMode.OneWay });
            grid.Children.Add(icon);
        }
        presenter.Content = grid;
    }
}
