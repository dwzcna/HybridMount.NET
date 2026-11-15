using HybridMount.Models.Mounting;
using System.Runtime.ExceptionServices;
using System.ComponentModel;
using System.Security.Cryptography;
namespace HybridMount.Models.Mounting;

/// <summary>根锁内捕获的上传记录及文件戳；只有锁外摘要与捕获内容一致才携带恢复凭据。</summary>
internal sealed record UploadCommitCapture(LocalUpload Row,LocalFileStamp? Stamp,bool NeedsHash)
{
    internal LocalFileStamp? VerifiedStamp {get;init;}
    /// <summary>完整只读摘要且最终戳稳定时才证明存在后续正文；缺失证明不得重新发送。</summary>
    internal bool ContentDifferent {get;init;}
}
