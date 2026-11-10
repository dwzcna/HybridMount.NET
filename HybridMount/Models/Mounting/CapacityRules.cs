using System.Globalization;
namespace HybridMount.Models.Mounting;

/// <summary>完整远端逻辑用量；只有范围内全部枚举成功才可返回，不能来自部分本地索引。</summary>
public sealed record RemoteUsage(string Scope,long UsedBytes,bool IsComplete=true);

/// <summary>容量配置与字节运算的纯规则；不访问网络、文件或配置。</summary>
public static class CapacityRules
{
    public static bool Enabled(MountDefinition definition)=>definition.CapacityAutoDetectEnabled||definition.MaximumCapacityBytes is not null;
    /// <summary>自动识别适用于 DAV 与群晖；手动值为空表示不限，零与负数均拒绝。</summary>
    public static void Validate(FileSourceKind? kind,bool automatic,long? maximum)
    {
        if(automatic&&kind is not(FileSourceKind.WebDav or FileSourceKind.Synology))throw new ArgumentException("只有 WebDAV 和群晖支持自动识别容量。");
        if(maximum is <=0)throw new ArgumentException("最大容量必须大于零；留空表示不限。");
    }
    /// <summary>使用十进制 GB/TB 和 decimal 换算；不接受指数、负数、零、字节小数或溢出。</summary>
    public static long? ParseMaximum(string text,string unit)
    {
        if(string.IsNullOrWhiteSpace(text))return null;
        if(unit is not("GB" or "TB"))throw new ArgumentException("请选择 GB 或 TB。");
        if(!decimal.TryParse(text,NumberStyles.AllowDecimalPoint|NumberStyles.AllowLeadingWhite|NumberStyles.AllowTrailingWhite,CultureInfo.CurrentCulture,out var value)||value<=0)
            throw new ArgumentException("最大容量必须为有效正数；留空表示不限。");
        try
        {
            var bytes=checked(value*(unit=="TB"?1_000_000_000_000m:1_000_000_000m));
            if(bytes!=decimal.Truncate(bytes)||bytes>long.MaxValue)throw new OverflowException();
            return checked((long)bytes);
        }
        catch(OverflowException){throw new ArgumentException("最大容量超出范围或小于一个完整字节。");}
    }
    public static bool SameSettings(MountDefinition left,MountDefinition right)=>left.CapacityAutoDetectEnabled==right.CapacityAutoDetectEnabled&&left.MaximumCapacityBytes==right.MaximumCapacityBytes;
    /// <summary>仅正增长需要空间；已核实的等长和缩小更新不被现有超额阻挡。</summary>
    public static long Growth(long oldLength,long newLength)
    {ArgumentOutOfRangeException.ThrowIfNegative(oldLength);ArgumentOutOfRangeException.ThrowIfNegative(newLength);return newLength>oldLength?newLength-oldLength:0;}
}
