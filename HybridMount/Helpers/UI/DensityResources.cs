using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HybridMount.Helpers.UI;

/// <summary>应用拥有稳定尺寸调色板，窗口只拥有UI调度；切换不删除模板仍引用的键或替换隐式样式。</summary>
internal sealed class DensityResources(Grid root, Func<bool> compact, Action<Exception> report) : IDisposable
{
    private static readonly ConditionalWeakTable<Application, Palette> Palettes = new();
    private bool _pending, _disposed;
    internal Action<string>? Stage { get; set; }

    /// <summary>只由所属窗口Dispatcher使用；普通、冷启动和Popup共用同一组有类型的资源。</summary>
    internal void Request()
    {
        if (_disposed || _pending) return;
        _pending = true;
        if (!root.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _pending = false;
            if (_disposed) return;
            try
            {
                var desired = compact();
                Stage?.Invoke(desired ? "Density.Compact.Begin" : "Density.Standard.Begin");
                var palette = Palettes.GetValue(Application.Current, static app => new(app.Resources));
                if (palette.Apply(desired))
                {
                    RefreshTheme(root);
                    if (root.XamlRoot is { } xamlRoot)
                        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot))
                            if (popup.Child is FrameworkElement child) RefreshTheme(child);
                }
                Stage?.Invoke(desired ? "Density.Compact.End" : "Density.Standard.End");
            }
            catch (Exception error) { report(error); }
        })) _pending = false;
    }

    /// <summary>同一UI回调内还原原主题，重新计算ThemeResource；保留页面、模板、输入及弹窗。</summary>
    private static void RefreshTheme(FrameworkElement element)
    {
        var requested = element.RequestedTheme;
        try { element.RequestedTheme = element.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark; }
        finally { element.RequestedTheme = requested; }
    }

    /// <summary>仅包含SDK尺寸值，不持窗口或控件；覆盖字典随Application寿命保留，回切逐键恢复标准值。</summary>
    private sealed class Palette
    {
        private readonly ResourceDictionary _resources, _overrides = new();
        private readonly (object Key, object Standard, object Compact)[] _values;
        private bool _attached, _compact;
        internal Palette(ResourceDictionary resources)
        {
            _resources = resources;
            var source = new ResourceDictionary { Source = new Uri("ms-appx:///Microsoft.UI.Xaml/DensityStyles/Compact.xaml") };
            _values = source.Where(pair => pair.Key is string && pair.Value is double or Thickness).Select(pair =>
            {
                if (!resources.TryGetValue(pair.Key, out var standard) || standard is not (double or Thickness))
                    throw new InvalidOperationException("密度标准尺寸资源缺失或类型不匹配：" + pair.Key);
                var compactValue = pair.Key is "TreeViewItemPresenterPadding" or "TreeViewItemPresenterMargin" && pair.Value is double size
                    ? (object)new Thickness(size) : pair.Value;
                if (standard.GetType() != compactValue.GetType())
                    throw new InvalidOperationException("密度尺寸资源类型不一致：" + pair.Key);
                return (pair.Key, standard, compactValue);
            }).ToArray();
        }
        internal bool Apply(bool compact)
        {
            if (_compact == compact) return false;
            foreach (var item in _values) _overrides[item.Key] = compact ? item.Compact : item.Standard;
            if (!_attached) { _resources.MergedDictionaries.Add(_overrides); _attached = true; }
            _compact = compact;
            return true;
        }
    }
    public void Dispose() { _disposed = true; Stage = null; }
}
