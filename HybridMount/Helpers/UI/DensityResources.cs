using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HybridMount.Helpers.UI;

/// <summary>窗口拥有的密度尺寸覆盖；保留现有模板与隐式样式，离开选择回调后合并更新。</summary>
internal sealed class DensityResources(Grid root, Func<bool> compact, Action<Exception> report) : IDisposable
{
    private readonly ResourceDictionary _overrides = new();
    private readonly ResourceDictionary _resources = Application.Current.Resources;
    private KeyValuePair<object, object>[]? _compactValues;
    private bool _pending, _disposed, _active, _attached;

    /// <summary>仅在窗口 Dispatcher 更新尺寸资源；不将 SDK 的隐式 TextBlock 样式动态接入或移出已加载树。</summary>
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
                if (desired == _active) return;
                if (!_attached)
                {
                    var source = new ResourceDictionary { Source = new Uri("ms-appx:///Microsoft.UI.Xaml/DensityStyles/Compact.xaml") };
                    // Compact.xaml 还包含隐式 Style；这里只采用有明确名称的原生尺寸值。
                    _compactValues = source.Where(pair => pair.Key is string && pair.Value is double or Thickness)
                        // SDK 紧凑字典把这两个 Thickness 键编为 Double(0)，恢复模板所需类型。
                        .Select(pair => pair.Key is "TreeViewItemPresenterPadding" or "TreeViewItemPresenterMargin" && pair.Value is double size
                            ? new KeyValuePair<object, object>(pair.Key, new Thickness(size)) : pair).ToArray();
                    _resources.MergedDictionaries.Add(_overrides);
                    _attached = true;
                }
                if (desired)
                    foreach (var pair in _compactValues!) _overrides[pair.Key] = pair.Value;
                else
                    _overrides.Clear();
                // 弹窗在独立 Popup 树中，只覆盖窗口资源会遗漏；应用级尺寸配合逐树主题重取。
                RefreshTheme(root);
                if (root.XamlRoot is { } xamlRoot)
                    foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot))
                        if (popup.Child is FrameworkElement child) RefreshTheme(child);
                _active = desired;
            }
            catch (Exception error) { report(error); }
        })) _pending = false;
    }

    /// <summary>同一UI回调内恢复原主题，重新计算 ThemeResource 尺寸；不替换模板或重建草稿。</summary>
    private static void RefreshTheme(FrameworkElement element)
    {
        var requested = element.RequestedTheme;
        try { element.RequestedTheme = element.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark; }
        finally { element.RequestedTheme = requested; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_attached) _resources.MergedDictionaries.Remove(_overrides);
    }
}
