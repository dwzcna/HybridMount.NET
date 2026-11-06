using System.Runtime.ExceptionServices;
using System.ComponentModel;
using System.Security.Cryptography;
using HybridMount.Models.Mounting;

namespace HybridMount.Services.Mounting;

internal sealed record UploadCommitCapture(LocalUpload Row,LocalFileStamp? Stamp,bool NeedsHash)
{
    internal LocalFileStamp? VerifiedStamp {get;init;}
}

internal interface IRecoveryUploadCommitter
{
    bool NeedsRecovery(MountDefinition mount,MountEntry entry,LocalUpload prepared,LocalFileStamp stamp);
    LocalFileStamp? Commit(MountDefinition mount,MountEntry entry,LocalUpload prepared,LocalFileStamp? verifiedStamp);
}

internal static class UploadCommitRecovery
{
    internal static bool SameStamp(string path,LocalFileStamp stamp)
    {
        try{return LocalFileState.Read(path).Key==stamp.Key;}
        catch(Win32Exception error)when(error.NativeErrorCode is 2 or 3){return false;}
    }
    internal static async Task<UploadCommitCapture?> VerifyAsync(SemaphoreSlim gate,Func<UploadCommitCapture?> capture,Func<UploadCommitCapture,bool> current,Func<UploadCommitCapture,Task<string>> hash,CancellationToken token)
    {
        UploadCommitCapture? attempt=null;string? digest=null;ExceptionDispatchInfo? failure=null;
        return await UploadPreparation.RunAsync<UploadCommitCapture>(gate,()=> (attempt=capture()) is not null,async()=>
        {
            if(!attempt!.NeedsHash)return;
            try{digest=await hash(attempt).ConfigureAwait(false);}catch(Exception error){failure=ExceptionDispatchInfo.Capture(error);}
        },()=>
        {
            if(!current(attempt!))return null;
            failure?.Throw();
            return attempt! with{VerifiedStamp=attempt!.NeedsHash&&digest==attempt.Row.SnapshotSha256?attempt.Stamp:null};
        },()=>{},token).ConfigureAwait(false);
    }

    internal static async Task<string> HashAsync(string path,LocalFileStamp stamp,CancellationToken token,Func<Task>? checkpoint=null)
    {
        await using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,131072,FileOptions.Asynchronous|FileOptions.SequentialScan);
        if(LocalFileState.Read(input.SafeFileHandle).Key!=stamp.Key)throw new UploadPreparationChangedException();
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var buffer=new byte[131072];int count;
        while((count=await input.ReadAsync(buffer,token).ConfigureAwait(false))!=0)
        {
            hash.AppendData(buffer,0,count);
            if(checkpoint is not null)await checkpoint().ConfigureAwait(false);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
