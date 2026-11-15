using HybridMount;
using HybridMount.Models.Localization;
using HybridMount.Models.Recovery;
using HybridMount.UI;
using HybridMount.UI.Dispatch;
using HybridMount.UI.Localization;
using System.Collections.ObjectModel;
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
                "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:loc=\"using:HybridMount.UI\"><TextBlock loc:UserInterface.Source=\"{Binding}\" loc:UserInterface.Path=\"Display\" TextWrapping=\"Wrap\" IsTextSelectionEnabled=\"True\"/></DataTemplate>")
        };
        var feedback = new InfoBar { Style = (Style)Application.Current.Resources["InstantNotificationStyle"], IsClosable = true };
        var count = new TextBlock();
        Button Command(ResourceMessage message) => UserInterface.Configure(new Button { Style = (Style)Application.Current.Resources["SubtleButtonStyle"] }, ("Content", message));
        var selectAll = Command(ResourceMessage.Create("SelectAllEligibleItems")); var clearAll = Command(ResourceMessage.Create("DeselectAll"));
        var commands = new ActionPanel { Style = (Style)Application.Current.Resources["ActionPanelStyle"] };
        commands.Children.Add(selectAll); commands.Children.Add(clearAll);
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(UserInterface.Configure(new TextBlock { TextWrapping = TextWrapping.Wrap }, ("Text", global::HybridMount.Models.Localization.ResourceMessage.Create("ExplorerEntryExplanation"))));
        panel.Children.Add(commands); panel.Children.Add(count); panel.Children.Add(list); panel.Children.Add(feedback);
        var dialog = global::HybridMount.UI.UserInterface.Configure(new ContentDialog
        {
            XamlRoot = XamlRoot,Content = panel,DefaultButton = ContentDialogButton.Close,IsPrimaryButtonEnabled = false
        }, ("Title", global::HybridMount.Models.Localization.ResourceMessage.Create("ExplorerEntryTitle")), ("PrimaryButtonText", global::HybridMount.Models.Localization.ResourceMessage.Create("ExplorerEntryRemove")), ("SecondaryButtonText", global::HybridMount.Models.Localization.ResourceMessage.Create("ExplorerEntryReload")), ("CloseButtonText", global::HybridMount.Models.Localization.ResourceMessage.Create("Close")));
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
            UserInterface.Set(count, "Text", global::HybridMount.Models.Localization.ResourceMessage.Create("ItemsSelected", list.SelectedItems.Count));
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
            feedback.Severity = InfoBarSeverity.Informational; global::HybridMount.UI.UserInterface.Set(feedback, "Message", global::HybridMount.Models.Localization.ResourceMessage.Create("ExplorerEntrySearching")); feedback.IsOpen = true;
            try
            {
                var found = await App.Current.DiscoverExplorerEntriesAsync(cancel.Token);
                if (!active || _disposed || stopping() || cancel.IsCancellationRequested) return;
                list.SelectedItems.Clear(); rows.Clear(); foreach (var row in found) rows.Add(row);
                global::HybridMount.UI.UserInterface.Set(feedback, "Message", global::HybridMount.Models.Localization.ResourceMessage.Create(found.Count == 0 ? "ExplorerEntryNone" : "ExplorerEntryFound", found.Count));
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (active && !_disposed && !stopping() && !cancel.IsCancellationRequested)
                { list.SelectedItems.Clear(); rows.Clear(); feedback.Severity = InfoBarSeverity.Error; global::HybridMount.UI.UserInterface.Set(feedback, "Message", error.Message); feedback.IsOpen = true; }
            }
            finally { loading = false; UpdateSelection(); }
        }
        async void Reload(ContentDialog _, ContentDialogButtonClickEventArgs args) { args.Cancel = true; await ReloadAsync(); }
        list.SelectionChanged += Selection; list.ContainerContentChanging += PrepareRow;
        selectAll.Click += SelectAll; clearAll.Click += ClearAll;
        dialog.SecondaryButtonClick += Reload; dialog.Closing += Closing;
        try
        {
            var shown = UserInterface.ShowAsync(dialog);
            await ReloadAsync();
            var result = await shown;
            active = false;
            if (result != ContentDialogResult.Primary || _disposed || stopping() || cancel.IsCancellationRequested) return;
            var captured = list.SelectedItems.OfType<ExplorerEntryCandidate>().Where(x => x.CanRemove).ToArray();
            if (captured.Length == 0) return;
            var details = global::HybridMount.UI.Localization.UILanguageService.Messages.GetString("ExplorerEntryConfirm", global::HybridMount.UI.Localization.UILanguageService.ResourceCulture)! + "\n\n" + string.Join("\n\n", captured.Select(x => x.Display));
            _recoveryDialog = global::HybridMount.UI.UserInterface.Configure(new ContentDialog
            {
                XamlRoot = XamlRoot,DefaultButton = ContentDialogButton.Close,                Content = new ScrollViewer { MaxHeight = 420, Content = new TextBlock { Text = details, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } }            }, ("Title", global::HybridMount.Models.Localization.ResourceMessage.Create("ExplorerEntryTitle")), ("PrimaryButtonText", global::HybridMount.Models.Localization.ResourceMessage.Create("ExplorerEntryRemove")), ("CloseButtonText", global::HybridMount.Models.Localization.ResourceMessage.Create("Cancel")));
            if (await UserInterface.ShowAsync(_recoveryDialog) != ContentDialogResult.Primary || _disposed || stopping()) return;
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
        var dialog = global::HybridMount.UI.UserInterface.Configure(new ContentDialog
        {
            XamlRoot = XamlRoot,            Content = new ScrollViewer { Content = text, MaxHeight = 420 }        }, ("Title", global::HybridMount.Models.Localization.ResourceMessage.Create("ExplorerEntryTitle")), ("CloseButtonText", global::HybridMount.Models.Localization.ResourceMessage.Create("Cancel")));
        _recoveryDialog = dialog; var working = true;
        void Closing(ContentDialog _, ContentDialogClosingEventArgs args)
        {
            if (!working || _disposed || stopping()) return;
            args.Cancel = true; cancel.Cancel(); UserInterface.Set(dialog, "CloseButtonText", global::HybridMount.Models.Localization.ResourceMessage.Create("CancellationInProgress"));
        }
        dialog.Closing += Closing;
        var shown = UserInterface.ShowAsync(dialog);
        try
        {
            var results = await App.Current.RemoveExplorerEntriesAsync(captured, cancel.Token, (index, total, name) =>
                App.Current.UIDispatcher.TryEnqueue(() => { if (!_disposed && !stopping() && working) text.Text = $"{index}/{total}\n{name}"; }));
            if (!_disposed && !stopping()) text.Text = string.Join("\n\n", results.Select(x => x.Display));
        }
        catch (Exception error) { if (!_disposed && !stopping()) text.Text = error.Message; }
        finally { working = false; if (!_disposed && !stopping()) UserInterface.Set(dialog, "CloseButtonText", global::HybridMount.Models.Localization.ResourceMessage.Create("Close")); }
        await shown; dialog.Closing -= Closing;
    }
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;_recoveryCancel?.Cancel();_recoveryDialog?.Hide();
    }
}
