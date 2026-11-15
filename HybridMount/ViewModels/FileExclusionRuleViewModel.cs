using HybridMount.Models.Mounting;
using HybridMount.UI.Localization;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
namespace HybridMount.ViewModels;

/// <summary>规则编辑行；只保存页面草稿，控件变化不直接写配置或触发扫描。</summary>
public sealed class FileExclusionRuleViewModel : ObservableObject
{
    private int _matchIndex; private string _text;
    public FileExclusionRuleViewModel(FileExclusionRule rule) { _matchIndex = (int)rule.Match; _text = rule.Text; }
    public int MatchIndex { get => _matchIndex; set { if (value is >= 0 and <= 2) SetProperty(ref _matchIndex, value); } }
    public string Text { get => _text; set => SetProperty(ref _text, value); }
    public FileExclusionRule ToRule() => new((FileExclusionMatchKind)_matchIndex, _text);
}
