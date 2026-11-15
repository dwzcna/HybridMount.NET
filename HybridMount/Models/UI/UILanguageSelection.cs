using HybridMount.Models.Localization;
using System.Globalization;
using System.Resources;
using System.Runtime.InteropServices;
namespace HybridMount.Models.UI;

/// <summary>不可变的界面语言快照；后台工作可在接纳时保存此快照，不改变区域格式。</summary>
public sealed record UILanguageSelection(string Choice, string Effective)
{
    public bool IsSystem => Choice == "system";
    public CultureInfo Culture => CultureInfo.GetCultureInfo(Effective);
}
