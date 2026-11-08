namespace HybridMount.Models.Mounting;
/// <summary>用户偏好与当前Shell入口分开；按真实包身份与当前包绑定判断；非打包不通过文件类型关联接管其他应用。</summary>
public sealed record ShellThumbnailCapability(bool Available,bool Packaged,string Reason)
{
    public static ShellThumbnailCapability From(bool packaged,bool registered,bool queryFailed=false)=>queryFailed?new(false,packaged,"无法核对云缩略图处理器注册，请查看完整诊断。"):
        registered&&packaged?new(true,true,"已绑定独立云缩略图宿主；在线仅读本机缓存，缺失时仅尝试完整本地内容。"):
        new(false,packaged,packaged?"当前包未绑定云缩略图处理器。":"非打包根缺少受支持的MSIX云绑定，不提供缩略图开关；已保存偏好保持。");
}
