using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace HybridMount.UI;

/// <summary>页面主命令区沿用Segmented自然高度及原生模板；仅修改主区尺寸状态，不约束溢出菜单行高。</summary>
public static class PageCommandBarSizing
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(PageCommandBarSizing), new PropertyMetadata(false, Changed));
    private static readonly DependencyProperty ScopeProperty = DependencyProperty.RegisterAttached(
        "Scope", typeof(object), typeof(PageCommandBarSizing), new PropertyMetadata(null));
    public static bool GetEnabled(DependencyObject value) => (bool)value.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject value, bool enabled) => value.SetValue(EnabledProperty, enabled);
    private static void Changed(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is not CommandBar bar) return;
        (bar.GetValue(ScopeProperty) as Scope)?.Dispose();
        bar.SetValue(ScopeProperty, (bool)args.NewValue ? new Scope(bar) : null);
    }

    /// <summary>所有访问由UI Dispatcher执行；只观察直接Segmented和已知命令，卸载后解除订阅并拒绝迟到更新。</summary>
    private sealed class Scope : IDisposable
    {
        private readonly CommandBar _bar;
        private readonly Dictionary<AppBarButton, long> _buttons = [];
        private readonly Dictionary<AppBarButton, (FrameworkElement Root, double Height)> _normalized = [];
        private Segmented? _reference;
        private bool _pending, _loaded, _disposed;
        private long _generation;
        private double _height;

        internal Scope(CommandBar bar)
        {
            _bar = bar;
            // 当前Toolkit实测自然外框33DIP；无参照页面共用同一资源，有参照时直接校准。
            _height = (double)Application.Current.Resources["PageToolbarDefaultHeight"];
            SetResources();
            bar.Loaded += Loaded;
            bar.Unloaded += Unloaded;
            bar.ActualThemeChanged += ThemeChanged;
            bar.SizeChanged += SizeChanged;
            bar.Opening += Opening;
        }
        private void Loaded(object sender, RoutedEventArgs args)
        {
            _loaded = true;
            if (AncestorPage(_bar) is { } page && FindSegmented(page) is { } reference)
            {
                _reference = reference;
                reference.SizeChanged += ReferenceChanged;
            }
            Queue();
        }
        private void Unloaded(object sender, RoutedEventArgs args) => Stop();
        private void ThemeChanged(FrameworkElement sender, object args) => Queue();
        private void SizeChanged(object sender, SizeChangedEventArgs args)
        {
            // 原生样式或主区／溢出转移可在Loaded之后应用，尺寸偏离时重核一次，不逐帧轮询。
            if (Math.Abs(args.NewSize.Height - _height) <= .25) return;
            _normalized.Clear(); Queue();
        }
        private void Opening(object? sender, object args) => Queue();
        private void ReferenceChanged(object sender, SizeChangedEventArgs args) => Queue();
        private void ButtonLoaded(object sender, RoutedEventArgs args) => Queue();
        private void Queue()
        {
            if (_disposed || !_loaded || _pending) return;
            _pending = true; var generation = _generation;
            if (!_bar.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (_disposed || generation != _generation) return;
                _pending = false;
                if (!_loaded) return;
                if (_reference is { ActualHeight: > 0 } reference) _height = reference.ActualHeight;
                Apply();
            })) _pending = false;
        }
        /// <summary>主区和更多按钮使用同一最小高度；不设置Height／MaxHeight，不裁剪或缩放内容。</summary>
        private void SetResources()
        {
            _bar.Resources["AppBarThemeCompactHeight"] = _height;
            _bar.Resources["AppBarThemeMinHeight"] = _height;
            _bar.Resources["CommandBarMoreButtonMargin"] = new Thickness(14, 0, 14, 0);
            _bar.Resources["AppBarButtonContentViewboxMargin"] = new Thickness(12, 0, 0, 0);
            _bar.Resources["AppBarButtonContentViewboxCollapsedMargin"] = new Thickness(0);
        }
        private void Apply()
        {
            SetResources();
            foreach (var part in MainParts(_bar))
            {
                if (part.Name is "ContentRoot" or "PrimaryItemsControl") part.MinHeight = _height;
                if (part is Button { Name: "MoreButton" } more)
                {
                    more.MinHeight = _height;
                    more.Padding = new Thickness(14, 0, 14, 0);
                    more.VerticalContentAlignment = VerticalAlignment.Center;
                }
            }
            foreach (var button in _bar.PrimaryCommands.Concat(_bar.SecondaryCommands).OfType<AppBarButton>())
            {
                if (!_buttons.ContainsKey(button))
                {
                    _buttons.Add(button, button.RegisterPropertyChangedCallback(AppBarButton.IsInOverflowProperty, (_, _) => Queue()));
                    button.Loaded += ButtonLoaded;
                }
                Normalize(button);
            }
        }
        /// <summary>仅改原生主区状态里的垂直留白和最小高度；Overflow状态及其文字、图标、Padding保持原值。</summary>
        private void Normalize(AppBarButton button)
        {
            if (VisualTreeHelper.GetChildrenCount(button) == 0 || VisualTreeHelper.GetChild(button, 0) is not FrameworkElement root) return;
            if (_normalized.TryGetValue(button, out var previous) && ReferenceEquals(previous.Root, root) && Math.Abs(previous.Height - _height) < .1) return;
            foreach (var group in VisualStateManager.GetVisualStateGroups(root))
            {
                if (group.Name != "ApplicationViewStates") continue;
                foreach (var state in group.States.Where(state => state.Name is "LabelOnRight" or "LabelCollapsed"))
                {
                    if (state.Storyboard is null) continue;
                    foreach (var timeline in state.Storyboard.Children.OfType<ObjectAnimationUsingKeyFrames>())
                    {
                        var target = Storyboard.GetTargetName(timeline);
                        var property = Storyboard.GetTargetProperty(timeline);
                        foreach (var frame in timeline.KeyFrames.OfType<DiscreteObjectKeyFrame>())
                        {
                            if (target == "ContentRoot" && property == "MinHeight") frame.Value = _height;
                            else if (property == "Margin" && frame.Value is Thickness margin && target is "TextLabel" or "ContentViewbox" or "SubItemChevron")
                                frame.Value = new Thickness(margin.Left, 0, margin.Right, 0);
                        }
                    }
                }
                var current = group.CurrentState?.Name;
                if (!button.IsInOverflow && current is "LabelOnRight" or "LabelCollapsed")
                {
                    // 重启同一组尺寸状态以采用新值，悬停、禁用及焦点属于其他状态组并保持。
                    VisualStateManager.GoToState(button, "FullSize", false);
                    VisualStateManager.GoToState(button, current, false);
                }
            }
            foreach (var part in MainParts(button))
            {
                if (part is TextBlock { Name: "TextLabel" } label) label.VerticalAlignment = VerticalAlignment.Center;
                // 原模板的两行均为Auto，去掉垂直Margin后须把剩余空间交给图文所在第一行。
                if (part is Grid { Name: "ContentRoot" } grid && grid.RowDefinitions.Count > 0)
                    grid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            }
            _normalized[button] = (root, _height);
        }
        private static Page? AncestorPage(DependencyObject element)
        {
            for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                if (parent is Page page) return page;
            return null;
        }
        private static Segmented? FindSegmented(DependencyObject element)
        {
            if (element is Segmented reference) return reference;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
                if (FindSegmented(VisualTreeHelper.GetChild(element, index)) is { } child) return child;
            return null;
        }
        /// <summary>只遍历当前控件的主模板，不进入嵌套命令或Popup；不查询窗口、数据库或网络。</summary>
        private static IEnumerable<FrameworkElement> MainParts(DependencyObject element)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            {
                var child = VisualTreeHelper.GetChild(element, index);
                if (child is AppBarButton or AppBarSeparator or Microsoft.UI.Xaml.Controls.Primitives.Popup) continue;
                if (child is FrameworkElement part) yield return part;
                foreach (var descendant in MainParts(child)) yield return descendant;
            }
        }
        private void Stop()
        {
            ++_generation; _pending = false; _loaded = false;
            if (_reference is not null) _reference.SizeChanged -= ReferenceChanged;
            _reference = null;
            foreach (var (button, token) in _buttons)
            {
                button.Loaded -= ButtonLoaded;
                button.UnregisterPropertyChangedCallback(AppBarButton.IsInOverflowProperty, token);
            }
            _buttons.Clear(); _normalized.Clear();
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; Stop();
            _bar.Loaded -= Loaded; _bar.Unloaded -= Unloaded;
            _bar.ActualThemeChanged -= ThemeChanged; _bar.SizeChanged -= SizeChanged; _bar.Opening -= Opening;
        }
    }
}
