namespace HybridMount.Models.Diagnostics;

/// <summary>将本地日历日转换为UTC半开区间，分别解析两端，支持夏令时的23／25小时日期。</summary>
internal static class LocalCalendarDay
{
    internal static (DateTimeOffset? From,DateTimeOffset? To) Bounds(DateTimeOffset? selected,TimeZoneInfo zone)
    {
        if(selected is not {} value)return (null,null);
        var day=DateTime.SpecifyKind(value.Date,DateTimeKind.Unspecified);
        return (Start(day,zone),Start(day.AddDays(1),zone));
    }
    private static DateTimeOffset Start(DateTime day,TimeZoneInfo zone)
    {
        // 极少数时区在午夜跳时，向前找到当天首个有效时刻；重复午夜取较早UTC时刻。
        for(var minute=0;zone.IsInvalidTime(day)&&minute<1440;minute++)day=day.AddMinutes(1);
        if(zone.IsAmbiguousTime(day))return new DateTimeOffset(day,zone.GetAmbiguousTimeOffsets(day).Max()).ToUniversalTime();
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(day,zone));
    }
}
