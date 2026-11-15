using HybridMount.Commons.Protocols;
using HybridMount.Models.Mounting;
using HybridMount.Models.Configuration;
using HybridMount.Models.Protocols.WebDAV;
namespace HybridMount.Services.Mounting;

/// <summary>本次认证的单次交接凭据；Dispose只归还尚未转交的票据和接纳保留位。</summary>
internal sealed class ConnectionSubmissionProof(object owner,ConnectionSubmissionDraft draft,DateTimeOffset started,DateTimeOffset finished,
    SourceCredentials credentials,IAuthenticationSession? session,Action release):IAsyncDisposable
{
    internal object Owner {get;}=owner;
    private ConnectionSubmissionDraft? _draft=draft;
    internal ConnectionSubmissionDraft Draft=>_draft??throw new ObjectDisposedException(nameof(ConnectionSubmissionProof));
    internal DateTimeOffset Started {get;}=started;
    internal DateTimeOffset Finished {get;}=finished;
    private SourceCredentials? _credentials=credentials;
    internal SourceCredentials Credentials=>_credentials??throw new ObjectDisposedException(nameof(ConnectionSubmissionProof));
    private IAuthenticationSession? _session=session;
    internal IAuthenticationSession? Session=>_session;
    internal bool Transferred {get;private set;}
    private bool _disposed;
    internal void Transfer(){if(_disposed||Transferred||DateTimeOffset.UtcNow-Finished>TimeSpan.FromMinutes(4))throw new IOException("认证结果已失效，请重新提交。");Transferred=true;}
    public async ValueTask DisposeAsync(){if(_disposed)return;_disposed=true;if(!Transferred){try{if(Session is {} ticket)await ticket.DisposeAsync().ConfigureAwait(false);}finally{release();ClearSecrets();}}}
    /// <summary>工作真正收尾后解除凭据和高级选项引用；不能由已经关闭的表单提前清除后台资料。</summary>
    internal void ClearSecrets(){_draft=null;_credentials=null;_session=null;}
    public override string ToString()=>"单次只读认证结果";
}
