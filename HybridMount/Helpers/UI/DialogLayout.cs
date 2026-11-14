using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HybridMount.Helpers.UI;

/// <summary>所有应用弹窗共用可见外框尺寸；只更新原生模板约束，不替换模板或把按钮放进内容滚动区。</summary>
internal static class DialogLayout
{
    private static readonly ConditionalWeakTable<ContentDialog, Lifetime> Lifetimes = new();

    /// <summary>显示前在 UI 线程设置资源并订阅尺寸变化；重复显示同一弹窗复用生命周期。</summary>
    internal static void Prepare(ContentDialog dialog) => Lifetimes.GetValue(dialog, static value => new Lifetime(value)).Start();
    internal static void Release(ContentDialog dialog) { if (Lifetimes.TryGetValue(dialog, out var lifetime)) lifetime.Stop(); }

    private sealed class Lifetime
    {
        private readonly ContentDialog _dialog;
        private XamlRoot? _root;
        internal Lifetime(ContentDialog dialog) { _dialog = dialog; }
        internal void Start()
        {
            Stop();
            _root = _dialog.XamlRoot ?? throw new InvalidOperationException("对话框缺少所属窗口。");
            _root.Changed += Changed;
            _dialog.Opened += Opened;
            _dialog.Closed += Closed;
            Update();
        }
        private void Changed(XamlRoot sender, XamlRootChangedEventArgs args) => Update();
        private void Opened(ContentDialog sender, ContentDialogOpenedEventArgs args) => Update();
        private void Closed(ContentDialog sender, ContentDialogClosedEventArgs args) => Stop();
        private void Update()
        {
            if (_root is null) return;
            var height = _root.Size.Height * .9;
            _dialog.Resources["ContentDialogMinWidth"] = 550d;
            _dialog.Resources["ContentDialogMaxWidth"] = 550d;
            _dialog.Resources["ContentDialogMaxHeight"] = height;
            // ThemeResource 在打开后未必重新求值；更新同一个模板容器，覆盖默认 548 宽度和 756 高度。
            if (FindBackground(_dialog) is { } background)
            {
                background.MinWidth = background.MaxWidth = background.Width = 550;
                background.MinHeight = Math.Min(184, height);
                background.MaxHeight = height;
                background.VerticalAlignment = VerticalAlignment.Center;
            }
        }
        private static FrameworkElement? FindBackground(DependencyObject element)
        {
            if (element is FrameworkElement { Name: "BackgroundElement" } found) return found;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
                if (FindBackground(VisualTreeHelper.GetChild(element, index)) is { } child) return child;
            return null;
        }
        internal void Stop()
        {
            if (_root is null) return;
            _root.Changed -= Changed;
            _dialog.Opened -= Opened;
            _dialog.Closed -= Closed;
            _root = null;
        }
    }
}
