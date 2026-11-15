using HybridMount.Commons.Persistence;
using HybridMount.Infrastructure.Persistence;
using HybridMount.Infrastructure.Protocols.S3;
using HybridMount.Infrastructure.Protocols.WebDAV;
using Microsoft.Data.Sqlite;
namespace HybridMount.Infrastructure.Persistence.Protocols;

/// <summary>协议专用持久操作的装配边界；SQL 由模块拥有，连接和事务始终由公共仓库控制。</summary>
internal interface IMountProtocolStorage
{
    string SchemaSql {get;}
    string DeleteEntriesSql=>"";
    string ActivateScopeSql=>"";
    string? PendingPredicate=>null;
    void CompleteHint(MountRepository repository,SqliteConnection db,SqliteTransaction transaction,Guid mount,string scope,string id) { }
}
/// <summary>仅集中装配模块，不在公共层实现协议报文、SDK、签名或查询规则。</summary>
internal static class ProtocolStorageModules
{
    internal static IReadOnlyList<IMountProtocolStorage> Storage {get;}=[new WebDavStorage(),new S3Storage()];
}
