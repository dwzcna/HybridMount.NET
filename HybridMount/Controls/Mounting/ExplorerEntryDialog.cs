using System.Collections.ObjectModel;
using HybridMount.Helpers.UI;
using HybridMount.Models.Mounting.Recovery;
using HybridMount.Services.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HybridMount.Controls.Mounting;

internal sealed class ExplorerEntryDialog(FrameworkElement owner, Func<bool> stopping) : IDisposable
{
    /// <summary>独立入口结果视图只允许移除注册。关闭检索即取消，应用服务持有后台操作的退出和收尾。</summary>
    private bool _disposed;
    private ContentDialog? _recoveryDialog;
    private CancellationTokenSource? _recoveryCancel;
    private XamlRoot XamlRoot => owner.XamlRoot;
    public async Task ShowAsync()
    {
        using var cancel = new CancellationTokenSource();
        _recoveryCancel = cancel;
        var rows = new ObservableCollection<ExplorerEntryCandidate>();
        var list = new ListView
        {
            ItemsSource = rows, SelectionMode = ListViewSelectionMode.Multiple, IsMultiSelectCheckBoxEnabled = true,
            MaxHeight = 330,
            ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
                "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:loc=\"using:HybridMount.Helpers.UI\"><TextBlock loc:Ui.Source=\"{Binding}\" loc:Ui.Path=\"Display\" TextWrapping=\"Wrap\" IsTextSelectionEnabled=\"True\"/></DataTemplate>")
        };
        var feedback = new InfoBar { Style = (Style)Application.Current.Resources["InstantNotificationStyle"], IsClosable = true };
        var count = new TextBlock();
        Button Command(string key) => Ui.Configure(new Button { Style = (Style)Application.Current.Resources["SubtleButtonStyle"] }, ("Content", UiText.Message(key)));
        var selectAll = Command("Text_BE146CE5330F"); var clearAll = Command("Text_F4D4BAE588C4");
        var commands = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        commands.Children.Add(selectAll); commands.Children.Add(clearAll);
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(Ui.Configure(new TextBlock { TextWrapping = TextWrapping.Wrap }, ("Text", UiText.Message("ExplorerEntryExplanation"))));
        panel.Children.Add(commands); panel.Children.Add(count); panel.Children.Add(list); panel.Children.Add(feedback);
        var dialog = Ui.Watch(new ContentDialog
        {
            XamlRoot = XamlRoot, Title = UiText.Text("ExplorerEntryTitle"), Content = panel,
            PrimaryButtonText = UiText.Text("ExplorerEntryRemove"), SecondaryButtonText = UiText.Text("ExplorerEntryReload"),
            CloseButtonText = UiText.Text("Text_3FD47EDCE45B"), DefaultButton = ContentDialogButton.Close, IsPrimaryButtonEnabled = false
        }, "Title", "PrimaryButtonText", "SecondaryButtonText", "CloseButtonText");
        _recoveryDialog = dialog;
        var loading = false; var active = true; var changing = false;
        void UpdateSelection()
        {
            if (!active || _disposed || stopping()) return;
            if (!changing)
            {
                changing = true;
                try { foreach (var row in list.SelectedItems.OfType<ExplorerEntryCandidate>().Where(x => !x.CanRemove).ToArray()) list.SelectedItems.Remove(row); }
                finally { changing = false; }
            }
            Ui.Set(count, "Text", UiText.Message("Text_5A5C8318EF64", list.SelectedItems.Count));
            dialog.IsPrimaryButtonEnabled = !loading && list.SelectedItems.Count > 0;
            dialog.IsSecondaryButtonEnabled = selectAll.IsEnabled = clearAll.IsEnabled = !loading;
        }
        void Selection(object _, SelectionChangedEventArgs __) => UpdateSelection();
        void PrepareRow(ListViewBase _, ContainerContentChangingEventArgs change) =>
            change.ItemContainer.IsEnabled = change.InRecycleQueue || change.Item is ExplorerEntryCandidate { CanRemove: true };
        void SelectAll(object _, RoutedEventArgs __) { foreach (var row in rows.Where(x => x.CanRemove)) if (!list.SelectedItems.Contains(row)) list.SelectedItems.Add(row); }
        void ClearAll(object _, RoutedEventArgs __) => list.SelectedItems.Clear();
        void Closing(ContentDialog _, ContentDialogClosingEventArgs args) { if (args.Result != ContentDialogResult.Primary) cancel.Cancel(); }
        async Task ReloadAsync()
        {
            if (loading || cancel.IsCancellationRequested) return;
            loading = true; UpdateSelection();
            feedback.Severity = InfoBarSeverity.Informational; feedback.Message = UiText.Text("ExplorerEntrySearching"); feedback.IsOpen = true;
            try
            {
                var found = await App.Current.DiscoverExplorerEntriesAsync(cancel.Token);
                if (!active || _disposed || stopping() || cancel.IsCancellationRequested) return;
                list.SelectedItems.Clear(); rows.Clear(); foreach (var row in found) rows.Add(row);
                feedback.Message = UiText.Text(found.Count == 0 ? "ExplorerEntryNone" : "ExplorerEntryFound", found.Count);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (active && !_disposed && !stopping() && !cancel.IsCancellationRequested)
                { list.SelectedItems.Clear(); rows.Clear(); feedback.Severity = InfoBarSeverity.Error; feedback.Message = error.Message; feedback.IsOpen = true; }
            }
            finally { loading = false; UpdateSelection(); }
        }
        async void Reload(ContentDialog _, ContentDialogButtonClickEventArgs args) { args.Cancel = true; await ReloadAsync(); }
        list.SelectionChanged += Selection; list.ContainerContentChanging += PrepareRow;
        selectAll.Click += SelectAll; clearAll.Click += ClearAll;
        dialog.SecondaryButtonClick += Reload; dialog.Closing += Closing;
        try
        {
            var shown = Ui.ShowAsync(dialog);
            await ReloadAsync();
            var result = await shown;
            active = false;
            if (result != ContentDialogResult.Primary || _disposed || stopping() || cancel.IsCancellationRequested) return;
            var captured = list.SelectedItems.OfType<ExplorerEntryCandidate>().Where(x => x.CanRemove).ToArray();
            if (captured.Length == 0) return;
            var details = UiText.Text("ExplorerEntryConfirm") + "\n\n" + string.Join("\n\n", captured.Select(x => x.Display));
            _recoveryDialog = Ui.Watch(new ContentDialog
            {
                XamlRoot = XamlRoot, Title = UiText.Text("ExplorerEntryTitle"), DefaultButton = ContentDialogButton.Close,
                Content = new ScrollViewer { MaxHeight = 420, Content = new TextBlock { Text = details, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } },
                PrimaryButtonText = UiText.Text("ExplorerEntryRemove"), CloseButtonText = UiText.Text("Text_2CD0F3BE8738")
            }, "Title", "PrimaryButtonText", "CloseButtonText");
            if (await Ui.ShowAsync(_recoveryDialog) != ContentDialogResult.Primary || _disposed || stopping()) return;
            await RemoveExplorerEntriesAsync(captured, cancel);
        }
        finally
        {
            active = false; cancel.Cancel();
            list.SelectionChanged -= Selection; list.ContainerContentChanging -= PrepareRow;
            selectAll.Click -= SelectAll; clearAll.Click -= ClearAll;
            dialog.SecondaryButtonClick -= Reload; dialog.Closing -= Closing;
            _recoveryCancel = null; _recoveryDialog = null;
        }
    }

    /// <summary>取消只阻止后续入口；当前入口等待注册写入与结果持久化完成，不提前报告成功。</summary>
    private async Task RemoveExplorerEntriesAsync(ExplorerEntryCandidate[] captured, CancellationTokenSource cancel)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var dialog = Ui.Watch(new ContentDialog
        {
            XamlRoot = XamlRoot, Title = UiText.Text("ExplorerEntryTitle"),
            Content = new ScrollViewer { Content = text, MaxHeight = 420 }, CloseButtonText = UiText.Text("Text_2CD0F3BE8738")
        }, "Title", "CloseButtonText");
        _recoveryDialog = dialog; var working = true;
        void Closing(ContentDialog _, ContentDialogClosingEventArgs args)
        {
            if (!working || _disposed || stopping()) return;
            args.Cancel = true; cancel.Cancel(); Ui.Set(dialog, "CloseButtonText", UiText.Message("Text_E8E08B0F61DD"));
        }
        dialog.Closing += Closing;
        var shown = Ui.ShowAsync(dialog);
        try
        {
            var results = await App.Current.RemoveExplorerEntriesAsync(captured, cancel.Token, (index, total, name) =>
                App.Current.UiDispatcher.TryEnqueue(() => { if (!_disposed && !stopping() && working) text.Text = $"{index}/{total}\n{name}"; }));
            if (!_disposed && !stopping()) text.Text = string.Join("\n\n", results.Select(x => x.Display));
        }
        catch (Exception error) { if (!_disposed && !stopping()) text.Text = error.Message; }
        finally { working = false; if (!_disposed && !stopping()) Ui.Set(dialog, "CloseButtonText", UiText.Message("Text_3FD47EDCE45B")); }
        await shown; dialog.Closing -= Closing;
    }
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;_recoveryCancel?.Cancel();_recoveryDialog?.Hide();
    }
}
