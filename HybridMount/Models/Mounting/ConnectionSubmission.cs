using HybridMount.Models.Configuration;
using HybridMount.Models.Protocols.WebDAV;
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
/// <summary>只含可公开的认证失败信息；UI仍可提供证书确认或验证码输入，不保存失败草稿。</summary>
internal sealed class ConnectionSubmissionException(string message,HybridMount.Models.Protocols.WebDAV.WebDavCertificateInfo? certificate=null,
    string? fingerprint=null,int? authenticationCode=null):IOException(message)
{
    internal HybridMount.Models.Protocols.WebDAV.WebDavCertificateInfo? Certificate {get;}=certificate;
    internal string? Fingerprint {get;}=fingerprint;
    internal int? AuthenticationCode {get;}=authenticationCode;
}
internal sealed record AcceptedApplicationTask(string TaskId,Guid? MountId=null);
