using System.Globalization;

namespace HybridMount.Models.Mounting;

/// <summary>仅格式化任务显示，使用十进制单位且最多两位小数；计算与持久化仍使用原始字节。</summary>
public static class TaskByteFormat
{
    /// <summary>实际传输速率使用十进制单位和一位小数；非有限或负样本不显示。</summary>
    public static string Speed(double bytesPerSecond)
    {
        if(!double.IsFinite(bytesPerSecond)||bytesPerSecond<0)return "";
        var unit=0;while(bytesPerSecond>=1000&&unit<Units.Length-1){bytesPerSecond/=1000;unit++;}
        return bytesPerSecond.ToString("0.0",CultureInfo.CurrentCulture)+Unit(unit)+"/s";
    }
    private static readonly string[] Units=["B","kB","MB","GB","TB","PB","EB"];
    private static string Unit(int index)=>index==0?global::HybridMount.Services.Localization.UiText.Text("Text_B1C5E73E1471"):Units[index];
    /// <summary>非负 Int64 字节使用当前区域数字格式；负进度按零显示，不转换输入数据。</summary>
    public static string Format(long bytes)
    {
        decimal value=Math.Max(0,bytes);var unit=0;
        while(value>=1000&&unit<Units.Length-1){value/=1000;unit++;}
        value=decimal.Round(value,2,MidpointRounding.AwayFromZero);
        if(value>=1000&&unit<Units.Length-1){value/=1000;unit++;}
        return value.ToString(unit==0?"N0":"0.##",CultureInfo.CurrentCulture)+" "+Unit(unit);
    }
}

/// <summary>挂载页的操作许可；每挂载忙碌只影响其自身，上传不构成页面忙碌。</summary>
public sealed record MountActionState(bool CanOpen,bool CanConnect,bool CanCancelConnection,bool CanDisconnect,
    bool CanEdit,bool CanRunConnected,bool CanRemove,string ConnectText,string RemoveText)
{
    /// <summary>根据真实会话及连接状态计算按钮；移除未完成时只允许继续原模式。</summary>
    public static MountActionState From(MountSnapshot? mount,bool busy=false,bool stopping=false)
    {
        if(mount is null||stopping)return new(false,false,false,false,false,false,false,global::HybridMount.Services.Localization.UiText.Text("Text_A5574109F020"),global::HybridMount.Services.Localization.UiText.Text("Text_FEE3C5717BB4"));
        var pending=mount.Definition.RemovalPending;
        var removing=mount.ActiveOperation is "remove" or "clearAndRemove";
        var available=!busy&&mount.Status!="检查中"&&!mount.IsConnecting&&!mount.IsDisconnecting&&!removing;
        var edit=available&&!pending&&mount.Definition.Source is not null;
        var connected=available&&!pending&&mount.IsConnected;
        var remove=pending?mount.Definition.RemovalMode==MountRemovalMode.ClearContents?global::HybridMount.Services.Localization.UiText.Text("Text_3E9FBE03CE66"):global::HybridMount.Services.Localization.UiText.Text("Text_E2B1ACC016C0"):
            mount.Definition.RegistrationStage==MountRegistrationStage.NotRegistered?global::HybridMount.Services.Localization.UiText.Text("Text_E410A42CC339"):global::HybridMount.Services.Localization.UiText.Text("Text_FEE3C5717BB4");
        return new(true,edit&&!mount.IsConnected,mount.IsConnecting&&!mount.IsDisconnecting,
            connected,edit,connected&&mount.ActiveOperation is null,available,
            mount.IsConnecting?global::HybridMount.Services.Localization.UiText.Text("Text_EE9EBE523D42"):mount.Status is "异常" or "需要恢复"?global::HybridMount.Services.Localization.UiText.Text("Text_C059B61C6A28"):global::HybridMount.Services.Localization.UiText.Text("Text_A5574109F020"),remove);
    }
}
