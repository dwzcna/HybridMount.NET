namespace HybridMount.Models.Mounting;
/// <summary>用户偏好与当前Shell入口分开；无包身份也可使用已安装的本产品处理器，不据输出目录推断能力。</summary>
public sealed record ShellThumbnailCapability(bool Available,bool Packaged,string Reason)
{
    public static ShellThumbnailCapability From(bool packaged,bool registered,bool queryFailed=false)=>queryFailed?new(false,packaged,"无法核对云缩略图处理器注册，请查看完整诊断。"):
        registered?new(true,packaged,packaged?"已绑定本产品云缩略图入口。":"使用已安装MSIX注册的云缩略图入口；当前后台须管理同一挂载。"):
        new(false,packaged,packaged?"当前包未绑定云缩略图处理器。":"当前未安装可用的云缩略图处理器，纯便携模式不提供此开关。");
}
