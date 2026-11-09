namespace HybridMount.Commons;

/// <summary>可选的内建协议传输绑定，不改变公共文件源接口或实际探测的协议能力。</summary>
internal interface IMountNetworkSource
{
    MountQueueBinding? QueueBinding {get;}
    void BindQueue(MountQueueBinding binding);
}
