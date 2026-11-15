using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Text;
namespace HybridMount.UI;

/// <summary>UI线程页头排版。测量条件及宽度不变时幂等，保留既有命令、菜单、焦点和读屏身份。</summary>
internal sealed class HeaderLayoutCoordinator : IDisposable
{
    private readonly Grid _root;
    private readonly FrameworkElement _filters;
    private readonly CommandBar _actions;
    private readonly Action<bool> _setCompact;
    private readonly AppBarButton[] _commands;
    private readonly ICommandBarElement[] _elements;
    private readonly List<(AppBarButton Button, DependencyProperty Property, long Token)> _tokens = [];
    private readonly Dictionary<AppBarButton, double> _chrome = [];
    private bool _dirty = true, _pending, _disposed, _measuring;
    private long _generation;
    private double _compact, _expanded, _lastWidth = -1;
    private double[] _widths = [];
    private HeaderLayoutChoice? _previous;
    private string? _stamp;
    /// <summary>只读布局诊断，不参与决策；用于核验普通进度没有触发页头重测。</summary>
    internal long MeasurementCount { get; private set; }
    internal HeaderLayoutCoordinator(Grid root, FrameworkElement filters, CommandBar actions, Action<bool> setCompact)
    {
        _root = root; _filters = filters; _actions = actions; _setCompact = setCompact;
        _elements = actions.PrimaryCommands.ToArray(); _commands = _elements.OfType<AppBarButton>().ToArray();
        actions.IsDynamicOverflowEnabled = false;
        root.Loaded += Loaded; root.Unloaded += Unloaded; root.SizeChanged += Resized; root.ActualThemeChanged += ThemeChanged;
        filters.SizeChanged += FilterResized;
        foreach (var button in _commands)
            foreach (var property in new[] { UIElement.VisibilityProperty, AppBarButton.LabelProperty })
                _tokens.Add((button, property, button.RegisterPropertyChangedCallback(property, (_, _) => Invalidate())));
        Invalidate();
    }
    private void Loaded(object sender, RoutedEventArgs args) => Invalidate();
    private void Unloaded(object sender, RoutedEventArgs args) { _actions.IsOpen = false; ++_generation; _pending = false; }
    private void Resized(object sender, SizeChangedEventArgs args) => Queue();
    private void FilterResized(object sender, SizeChangedEventArgs args) { if (!_measuring) Invalidate(); }
    private void ThemeChanged(FrameworkElement sender, object args) => Invalidate();
    /// <summary>只标记可能变化；真正排版先核对测量条件，重复请求不会关菜单或转移按钮。</summary>
    internal void Invalidate() { _dirty = true; Queue(); }
    private void Queue()
    {
        if (_disposed || _pending) return;
        _pending = true; var generation = _generation;
        if (!_root.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_disposed || generation != _generation) return;
            _pending = false;
            if (_root.IsLoaded && _root.ActualWidth > 0) Arrange();
        })) _pending = false;
    }
    private void Arrange()
    {
        var stamp = Stamp();
        var measure = _stamp is null || _dirty && stamp != _stamp;
        _dirty = false;
        if (!measure && Math.Abs(_lastWidth - _root.ActualWidth) < .25) return;
        if (measure && _actions.IsOpen) { _dirty = true; _actions.IsOpen = false; Queue(); return; }
        if (measure) Measure();
        var choice = HeaderLayoutPolicy.Choose(_root.ActualWidth, _compact, _expanded, _widths, _previous, gap: _root.ColumnSpacing);
        var visible = _commands.Where(button => button.Visibility == Visibility.Visible).ToArray();
        var primary = CommandsWithSeparators(visible.Take(choice.Count));
        var secondary = CommandsWithSeparators(visible.Skip(choice.Count));
        var moving = !_actions.PrimaryCommands.SequenceEqual(primary) || !_actions.SecondaryCommands.SequenceEqual(secondary);
        var labelsChanged = _previous?.ShowLabels != choice.ShowLabels;
        // 只有真实布局需要转移命令时才关层，关闭栈返回后再应用，纯进度及等价尺寸变化保持菜单。
        if ((moving || labelsChanged) && _actions.IsOpen) { _actions.IsOpen = false; Queue(); return; }
        if (labelsChanged || measure) _setCompact(!choice.ShowLabels);
        if (moving)
        {
            RemoveForeign(_actions.PrimaryCommands, primary); RemoveForeign(_actions.SecondaryCommands, secondary);
            Reconcile(_actions.PrimaryCommands, primary); Reconcile(_actions.SecondaryCommands, secondary);
        }
        _actions.HorizontalAlignment = HorizontalAlignment.Right;
        var overflow = choice.Count < visible.Length ? CommandBarOverflowButtonVisibility.Visible : CommandBarOverflowButtonVisibility.Collapsed;
        if (_actions.OverflowButtonVisibility != overflow) _actions.OverflowButtonVisibility = overflow;
        _previous = choice; _lastWidth = _root.ActualWidth; _stamp = Stamp();
    }
    /// <summary>首轮测量既有主区模板；之后复用非文字宽度，只重测标签，不临时把溢出命令搬回主区。</summary>
    private void Measure()
    {
        _measuring = true;
        try
        {
            ++MeasurementCount;
            if (_chrome.Count == 0) _actions.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
            foreach (var button in _commands.Where(button => button.Visibility == Visibility.Visible))
            {
                var text = LabelWidth(button);
                if (!_chrome.ContainsKey(button))
                {
                    button.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
                    _chrome[button] = button.IsInOverflow ? 52 : Math.Max(40, button.DesiredSize.Width) - text;
                }
            }
            _widths = _commands.Where(button => button.Visibility == Visibility.Visible)
                .Select(button => Math.Ceiling(Math.Max(40, _chrome[button] + LabelWidth(button)))).ToArray();
            _setCompact(false); _filters.UpdateLayout(); _filters.Measure(new(double.PositiveInfinity, double.PositiveInfinity)); _expanded = Math.Ceiling(_filters.DesiredSize.Width);
            _setCompact(true); _filters.UpdateLayout(); _filters.Measure(new(double.PositiveInfinity, double.PositiveInfinity)); _compact = Math.Ceiling(_filters.DesiredSize.Width);
        }
        finally { _measuring = false; }
    }
    private static double LabelWidth(AppBarButton button)
    {
        var label = new TextBlock { Text = button.Label ?? "", FontFamily = button.FontFamily, FontSize = button.FontSize,
            FontWeight = button.FontWeight, FontStyle = button.FontStyle, CharacterSpacing = button.CharacterSpacing };
        label.Measure(new(double.PositiveInfinity, double.PositiveInfinity)); return label.DesiredSize.Width;
    }
    /// <summary>只读本地控件测量条件；忽略任务行、选中状态、字节和命令IsEnabled。</summary>
    private string Stamp()
    {
        var value = new StringBuilder().Append(Localization.UILanguageService.ResourceCulture.Name).Append('|').Append(_root.ActualTheme)
            .Append('|').Append(_root.XamlRoot?.RasterizationScale);
        foreach (var button in _commands) value.Append('|').Append(button.Label).Append(':').Append(button.Visibility).Append(':').Append(button.FontSize).Append(':').Append(button.FontWeight.Weight);
        foreach (var element in Visuals(_filters))
        {
            if (element is FrameworkElement framework) value.Append('|').Append(framework.Visibility);
            if (element is TextBlock text) value.Append(':').Append(text.Text).Append(':').Append(text.FontSize).Append(':').Append(text.FontWeight.Weight);
            if (element is Control control) value.Append(':').Append(control.Padding).Append(':').Append(control.MinHeight).Append(':').Append(control.FontSize);
        }
        return value.ToString();
    }
    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static void RemoveForeign(IList<ICommandBarElement> current, IReadOnlyCollection<ICommandBarElement> desired)
    { for (var index = current.Count - 1; index >= 0; index--) if (!desired.Contains(current[index])) current.RemoveAt(index); }
    private static void Reconcile(IList<ICommandBarElement> current, IReadOnlyList<ICommandBarElement> desired)
    {
        for (var index = 0; index < desired.Count; index++)
        {
            if (index < current.Count && ReferenceEquals(current[index], desired[index])) continue;
            var previous = current.IndexOf(desired[index]); if (previous >= 0) current.RemoveAt(previous);
            current.Insert(index, desired[index]);
        }
    }
    /// <summary>保留分区内相邻命令之间的原分隔符，不产生首尾孤立分隔线。</summary>
    private ICommandBarElement[] CommandsWithSeparators(IEnumerable<AppBarButton> selected)
    {
        var buttons = selected.ToHashSet(); var result = new List<ICommandBarElement>(); AppBarSeparator? pending = null;
        foreach (var element in _elements)
        {
            if (element is AppBarSeparator separator && separator.Visibility == Visibility.Visible) pending = separator;
            if (element is not AppBarButton button || !buttons.Contains(button)) continue;
            if (pending is not null && result.Count > 0) result.Add(pending);
            pending = null; result.Add(button);
        }
        return result.ToArray();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _actions.IsOpen = false; _disposed = true; ++_generation;
        _root.Loaded -= Loaded; _root.Unloaded -= Unloaded; _root.SizeChanged -= Resized; _root.ActualThemeChanged -= ThemeChanged;
        _filters.SizeChanged -= FilterResized;
        foreach (var item in _tokens) item.Button.UnregisterPropertyChangedCallback(item.Property, item.Token);
        _tokens.Clear();
    }
}
