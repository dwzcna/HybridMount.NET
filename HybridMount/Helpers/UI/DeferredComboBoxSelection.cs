using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HybridMount.Helpers.UI;

/// <summary>选择弹层关闭后再发布全局布局变化；只使用当前选择，释放或卸载后拒绝迟到回调。</summary>
internal sealed class DeferredComboBoxSelection : IDisposable
{
    private readonly ComboBox _picker;
    private readonly Action<string> _apply;
    private string? _pending;
    private bool _queued, _disposed;
    private long _generation;

    internal DeferredComboBoxSelection(ComboBox picker, Action<string> apply)
    {
        _picker = picker;
        _apply = apply;
        picker.SelectionChanged += Changed;
        picker.DropDownClosed += Closed;
        picker.Unloaded += Unloaded;
    }

    private void Changed(object sender, SelectionChangedEventArgs args)
    {
        if (_disposed || _picker.SelectedValue is not string value) return;
        _pending = value;
        // 不在原生 SelectionChanged 栈上同步改变所有控件的尺寸和状态。
        _picker.IsDropDownOpen = false;
        Queue();
    }

    private void Closed(object? sender, object args) => Queue();
    private void Unloaded(object sender, RoutedEventArgs args) { ++_generation; _pending = null; _queued = false; }
    private void Queue()
    {
        if (_disposed || _queued || _pending is null) return;
        var generation = _generation;
        _queued = true;
        if (!_picker.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_disposed || generation != _generation) return;
            _queued = false;
            if (!_picker.IsLoaded || _picker.IsDropDownOpen || _pending is not { } value) return;
            _pending = null;
            _apply(value);
        })) _queued = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ++_generation;
        _pending = null;
        _picker.SelectionChanged -= Changed;
        _picker.DropDownClosed -= Closed;
        _picker.Unloaded -= Unloaded;
    }
}
