using HybridMount.Models.Mounting.Synology;
namespace HybridMount.Models.Mounting;

/// <summary>UI一次捕获的独立连接草稿；只在内存流转，不序列化到任务、日志或恢复表。</summary>
internal sealed record ConnectionSubmissionDraft(Guid? ExistingId,string Name,string Path,bool AutoConnect,
    FileSourceConfiguration Source,SourceCredentials Credentials,IReadOnlyList<FileExclusionRule> Exclusions,MountCreationOptions Options)
{
    /// <summary>备份只成为独立新建草稿，不保存、不核验目录、不带认证或原运行身份。</summary>
    internal static ConnectionSubmissionDraft FromBackup(MountBackupItem item) => new(null, item.Name, item.LocalPath, item.AutoConnect, item.Source,
        item.Credentials, Array.AsReadOnly((item.ExclusionRules ?? []).ToArray()),
        new(item.ThumbnailsEnabled, item.RemoteAutoDetectEnabled, item.RemoteIncrementalSeconds, item.RemoteFullSeconds, item.CapacityAutoDetectEnabled,
            item.MaximumCapacityBytes, item.Sqs, item.SqsCredentials));
    internal ConnectionSubmissionDraft Freeze()=>this with{Exclusions=Array.AsReadOnly(Exclusions.ToArray())};
    internal bool SameInput(ConnectionSubmissionDraft other)=>ExistingId==other.ExistingId&&Name==other.Name&&Path==other.Path&&AutoConnect==other.AutoConnect&&Source==other.Source&&Credentials==other.Credentials&&Options==other.Options&&Exclusions.SequenceEqual(other.Exclusions);
    public override string ToString()=>"内存连接草稿";
}
/// <summary>本次认证的单次交接凭据；Dispose只归还尚未转交的票据和接纳保留位。</summary>
internal sealed class ConnectionSubmissionProof(object owner,ConnectionSubmissionDraft draft,DateTimeOffset started,DateTimeOffset finished,
    SourceCredentials credentials,SynologySessionTicket? session,Action release):IAsyncDisposable
{
    internal object Owner {get;}=owner;
    private ConnectionSubmissionDraft? _draft=draft;
    internal ConnectionSubmissionDraft Draft=>_draft??throw new ObjectDisposedException(nameof(ConnectionSubmissionProof));
    internal DateTimeOffset Started {get;}=started;
    internal DateTimeOffset Finished {get;}=finished;
    private SourceCredentials? _credentials=credentials;
    internal SourceCredentials Credentials=>_credentials??throw new ObjectDisposedException(nameof(ConnectionSubmissionProof));
    private SynologySessionTicket? _session=session;
    internal SynologySessionTicket? Session=>_session;
    internal bool Transferred {get;private set;}
    private bool _disposed;
    internal void Transfer(){if(_disposed||Transferred||DateTimeOffset.UtcNow-Finished>TimeSpan.FromMinutes(4))throw new IOException("认证结果已失效，请重新提交。");Transferred=true;}
    public async ValueTask DisposeAsync(){if(_disposed)return;_disposed=true;if(!Transferred){try{if(Session is {} ticket)await ticket.DisposeAsync().ConfigureAwait(false);}finally{release();ClearSecrets();}}}
    /// <summary>工作真正收尾后解除凭据和高级选项引用；不能由已经关闭的表单提前清除后台资料。</summary>
    internal void ClearSecrets(){_draft=null;_credentials=null;_session=null;}
    public override string ToString()=>"单次只读认证结果";
}
/// <summary>只含可公开的认证失败信息；UI仍可提供证书确认或验证码输入，不保存失败草稿。</summary>
internal sealed class ConnectionSubmissionException(string message,HybridMount.Models.Mounting.WebDAV.WebDavCertificateInfo? certificate=null,
    string? fingerprint=null,int? authenticationCode=null):IOException(message)
{
    internal HybridMount.Models.Mounting.WebDAV.WebDavCertificateInfo? Certificate {get;}=certificate;
    internal string? Fingerprint {get;}=fingerprint;
    internal int? AuthenticationCode {get;}=authenticationCode;
}
internal sealed record AcceptedApplicationTask(string TaskId,Guid? MountId=null);
