namespace HybridMount.Services.Mounting;

/// <summary>DAV 精确临时项日志的既有 v6 表，公共初始化不再内嵌协议表定义。</summary>
internal sealed class WebDavStorage : IMountProtocolStorage
{
    public string SchemaSql=>"CREATE TABLE IF NOT EXISTS remote_temporary(mount TEXT NOT NULL,scope TEXT NOT NULL,path TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(mount,scope,path));";
}
