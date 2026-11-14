using HybridMount.Helpers.UI;
using HybridMount.Models.Mounting;
using HybridMount.Services.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace HybridMount.Controls.Mounting;

/// <summary>三协议共用只读能力列表；展开仅显示已经发布的内存证据，控件不持有协议客户端。</summary>
public sealed class ProtocolCapabilitiesView : UserControl
{
    private readonly Expander _expander = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _rows = new() { Spacing = 16 };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<string, (TextBlock Status, TextBlock Details)> _fields = [];
    private readonly Grid _probeActions = new() { ColumnSpacing = 12, RowSpacing = 8 };
    private readonly Button _probeButton = new();
    private readonly TextBlock _probeWarning = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _probeProgress = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly StackPanel _cleanupRows = new() { Spacing = 8 };
    private CapabilityProbeRun? _probe;
    private bool _probeRunning;
    public event EventHandler? ProbeRequested;
    public event EventHandler<Guid>? CleanupRequested;
    public event EventHandler? HistoryRequested;
    private FileSourceKind _kind;
    private bool _sqs, _history;
    private ProtocolCapabilitySnapshot? _snapshot;

    public ProtocolCapabilitiesView()
    {
        var panel = new StackPanel { Spacing = 8 };
        _expander.Content = _rows;
        _expander.Expanding += (_, _) => HistoryRequested?.Invoke(this, EventArgs.Empty);
        panel.Children.Add(_expander); panel.Children.Add(_hint);
        _probeActions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _probeActions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _probeActions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _probeActions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _probeActions.Children.Add(_probeButton); Grid.SetColumn(_probeWarning, 1); _probeActions.Children.Add(_probeWarning);
        _probeButton.Click += (_, _) => ProbeRequested?.Invoke(this, EventArgs.Empty);
        _probeActions.SizeChanged += (_, _) =>
        {
            var narrow = _probeActions.ActualWidth < 420;
            Grid.SetColumn(_probeWarning, narrow ? 0 : 1); Grid.SetRow(_probeWarning, narrow ? 1 : 0);
            Grid.SetColumnSpan(_probeWarning, narrow ? 2 : 1);
        };
        Loaded += (_, _) => _probeButton.Style = (Style)Application.Current.Resources["ProtocolSecondaryButtonStyle"];
        panel.Children.Add(_probeActions); panel.Children.Add(_probeProgress); panel.Children.Add(_cleanupRows); Content = panel;
        Ui.ObserveLanguage(this, Render);
        Render();
    }
    /// <summary>UI线程更新不可变快照；相同快照不重建行，保留展开状态和外层滚动位置。</summary>
    public void Show(FileSourceKind kind, ProtocolCapabilitySnapshot? snapshot, bool history, bool sqs)
    {
        if (_kind != kind || _sqs != sqs || _fields.Count == 0)
        {
            _kind = kind; _sqs = sqs; _rows.Children.Clear(); _rows.Children.Add(_summary); _fields.Clear();
            foreach (var item in ProtocolCapabilityCatalog.For(kind, sqs))
            {
                var block = new StackPanel { Spacing = 4 };
                var heading = new Grid { ColumnSpacing = 12 };
                heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                heading.Children.Add(new TextBlock { Text = item.Name, TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                var status = new TextBlock { MinWidth = 64, TextAlignment = TextAlignment.Right }; Grid.SetColumn(status, 1); heading.Children.Add(status);
                var details = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
                block.Children.Add(heading); block.Children.Add(details); _rows.Children.Add(block); _fields[item.Id] = (status, details);
            }
        }
        _snapshot = snapshot; _history = history; Render();
    }
    private void Render()
    {
        _expander.Header = UiText.Text("ProtocolCapabilitiesHeader");
        _expander.IsEnabled = true;
        _hint.Visibility = _snapshot is null ? Visibility.Visible : Visibility.Collapsed;
        _hint.Text = UiText.Text("ProtocolCapabilitiesHint");
        _summary.Text = UiText.Text(_history ? "ProtocolCapabilitiesHistory" : "ProtocolCapabilitiesScope");
        foreach (var retry in _cleanupRows.Children.OfType<StackPanel>().SelectMany(panel => panel.Children).OfType<Button>()) retry.IsEnabled = !_probeRunning;
        _probeButton.Content = UiText.Text(_probeRunning ? "CapabilityProbeCancel" : "CapabilityProbeStart");
        _probeWarning.Text = UiText.Text("CapabilityProbeWarning");
        _probeProgress.Text = _probe is null ? "" : UiText.Text("CapabilityProbeStage" + _probe.Stage) + "\n" +
            UiText.Text("CapabilityProbeSummary", _probe.Cases.Length, _probe.StartedUtc.ToLocalTime().ToString("g")) +
            (_probe.Cases.Any(item => item.PermissionDenied) ? "\n" + UiText.Text("CapabilityProbeWritePermission") : "");
        foreach (var item in ProtocolCapabilityCatalog.For(_kind, _sqs))
        {
            if (!_fields.TryGetValue(item.Id, out var field)) continue;
            var value = _snapshot is null ? null : ProtocolCapabilitySummary.Read(_snapshot, item.Id);
            field.Status.Text = UiText.Text("ProtocolStatus" + (value?.Status ?? ProtocolCapabilityStatus.Unknown));
            var text = UiText.Text(item.DescriptionKey);
            if (value?.Evidence.Count > 0)
            {
                var paths = value.Evidence.Select(e => e.Scope switch { "" => UiText.Text("ProtocolScopeRoot"), "@server" => UiText.Text("ProtocolScopeServer"), "@queue" => UiText.Text("ProtocolScopeQueue"), _ => e.Scope }).Distinct().Take(4);
                text += "\n" + UiText.Text("ProtocolEvidenceDetails", string.Join("；", paths), value.Evidence.Max(e => e.ObservedUtc).ToLocalTime().ToString("g"));
            }
            if (value is { Reason: not ProtocolCapabilityReason.None }) text += "\n" + UiText.Text("ProtocolReason" + value.Reason);
            field.Details.Text = text;
            if (_probe?.Cases.LastOrDefault(step => step.Id == item.Id) is {} step)
            {
                if (!step.Skipped || value?.Status is null or ProtocolCapabilityStatus.Unknown) field.Status.Text = UiText.Text("ProtocolStatus" + step.Status);
                field.Details.Text += "\n" + UiText.Text(step.Skipped ? "CapabilityProbeSkipped" : "CapabilityProbeCaseDetails",
                    ProbeReason(step.Reason), step.Milliseconds);
            }
        }
    }
    /// <summary>UI线程只切换接纳状态；能力清单在检测和清理期间仍可展开。</summary>
    internal void SetProbeAvailability(bool available, bool running)
    {
        _probeRunning = running; _probeButton.IsEnabled = available; Render();
    }
    internal void ShowProbe(CapabilityProbeRun? run) { _probe = run; Render(); }

    /// <summary>只展示持久记录；路径可复制，重试不会重新执行写入检测。</summary>
    internal void ShowProbeHistory(IReadOnlyList<CapabilityProbeRun> history)
    {
        _cleanupRows.Children.Clear();
        var candidates = history.Where(run => run.Resources.Any(item => item.Stage is not ("Cleaned" or "Planned"))).ToArray();
        if (candidates.Length == 0) return;
        var selection = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var details = new StackPanel { Spacing = 8 };
        foreach (var run in candidates)
            selection.Items.Add(new ComboBoxItem { Content = UiText.Text("CapabilityProbeCleanupRecord", run.StartedUtc.ToLocalTime().ToString("g"), run.RunId), Tag = run });
        selection.SelectionChanged += (_, _) =>
        {
            details.Children.Clear();
            if (selection.SelectedItem is not ComboBoxItem { Tag: CapabilityProbeRun run }) return;
            foreach (var item in run.Resources.Where(item => item.Stage is not ("Cleaned" or "Planned")))
                details.Children.Add(new TextBlock { Text = CapabilityProbePresentation.ResourceDetails(item), IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap });
            var retry = new Button { Content = UiText.Text("CapabilityProbeRetryCleanup"), IsEnabled = !_probeRunning,
                Style = (Style)Application.Current.Resources["ProtocolSecondaryButtonStyle"] };
            retry.Click += (_, _) => CleanupRequested?.Invoke(this, run.RunId);
            details.Children.Add(retry);
        };
        _cleanupRows.Children.Add(selection); _cleanupRows.Children.Add(details);
        selection.SelectedIndex = 0;
    }
    private static string ProbeReason(string reason) => string.IsNullOrEmpty(reason) ? UiText.Text("CapabilityProbeVerified") :
        UiText.Text("CapabilityProbeReason", reason);

}
