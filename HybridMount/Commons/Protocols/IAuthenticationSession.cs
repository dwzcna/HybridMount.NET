namespace HybridMount.Commons.Protocols;

/// <summary>认证交接所有者仅负责异步归还；具体协议会话不暴露给业务模型。</summary>
internal interface IAuthenticationSession:IAsyncDisposable { }
/// <summary>同一逻辑执行流的一次认证交接；包含null作用域，避免意外继承父流程票据。</summary>
internal static class AuthenticationSessionContext
{
    private static readonly AsyncLocal<IAuthenticationSession?> Ambient=new();
    internal static IAuthenticationSession? Current=>Ambient.Value;
    internal static IDisposable Use(IAuthenticationSession? session)
    {
        var before=Ambient.Value;Ambient.Value=session;return new Scope(before);
    }
    private sealed class Scope(IAuthenticationSession? before):IDisposable
    {
        public void Dispose()=>Ambient.Value=before;
    }
}
