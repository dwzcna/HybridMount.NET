namespace HybridMount.Models.Mounting;

/// <summary>Shell只读属性的原始值和SDK语义；未知或未完成状态不能冒充暂停或已同步。</summary>
internal sealed record ShellFileStatus(uint? ProviderState,uint? TransferStatus,uint? PlaceholderStatus,string? Error=null)
{
    public string ProviderName=>ProviderState switch
    {0=>"无提供程序",1=>"仅联机",2=>"已同步",3=>"已固定",4=>"等待上传",5=>"等待下载",6=>"传输中",7=>"错误",8=>"警告",9=>"已排除",10=>"待处理（未指定）",null=>"未提供",_=>"未知"};
    public string TransferName=>TransferStatus is null?"未提供":TransferStatus==0?"无传输标记":
        string.Join("、",new[]{(TransferStatus&8)!=0?"已暂停":null,(TransferStatus&512)!=0?"未完成":null,$"原始值 0x{TransferStatus:X}"}.OfType<string>());
    public override string ToString()=>$"Shell：StorageProviderState={ProviderState?.ToString()??"null"}（{ProviderName}）；SyncTransferStatus={TransferStatus?.ToString()??"null"}（{TransferName}）；FilePlaceholderStatus={PlaceholderStatus?.ToString()??"null"}"+(Error is null?"":"；查询失败="+Error);
}
