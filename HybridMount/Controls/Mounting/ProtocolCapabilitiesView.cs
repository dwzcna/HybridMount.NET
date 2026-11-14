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
    private FileSourceKind _kind;
    private bool _sqs, _history;
    private ProtocolCapabilitySnapshot? _snapshot;

    public ProtocolCapabilitiesView()
    {
        var panel = new StackPanel { Spacing = 8 };
        _expander.Content = _rows; panel.Children.Add(_expander); panel.Children.Add(_hint); Content = panel;
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
        _expander.IsEnabled = _snapshot is not null;
        if (_snapshot is null) _expander.IsExpanded = false;
        _hint.Visibility = _snapshot is null ? Visibility.Visible : Visibility.Collapsed;
        _hint.Text = UiText.Text("ProtocolCapabilitiesHint");
        _summary.Text = UiText.Text(_history ? "ProtocolCapabilitiesHistory" : "ProtocolCapabilitiesScope");
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
        }
    }
}
