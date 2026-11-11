using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
namespace HybridMount.Services.Localization;

/// <summary>当前SID、会话和包Family专属的内部语言摘要。只传语言／代次，不传路径、凭据或业务命令；原生热路径只读映射。</summary>
internal sealed class NativeUiLanguageBridge:IDisposable
{
    internal const uint Magic=0x484D4C47;
    private readonly MemoryMappedFile _map;private readonly MemoryMappedViewAccessor _view;private readonly object _gate=new();private int _revision;private bool _disposed;
    internal NativeUiLanguageBridge(string? name=null)
    {_map=MemoryMappedFile.CreateOrOpen(name??MappingName(),64,MemoryMappedFileAccess.ReadWrite);_view=_map.CreateViewAccessor(0,64,MemoryMappedFileAccess.ReadWrite);_revision=_view.ReadInt32(20)&~1;}
    /// <summary>奇数代次表示正在写，偶数提交；读者两次核验代次，应用保存成功后才调用。</summary>
    internal void Publish(UiLanguageSelection selection)
    {
        lock(_gate){if(_disposed)return;_revision=unchecked(_revision+2);_view.Write(20,unchecked((uint)(_revision|1)));Thread.MemoryBarrier();
            _view.Write(0,Magic);_view.Write(4,1u);_view.Write(8,selection.Effective switch{"en-US"=>0x0409u,"zh-HK"=>0x0c04u,_=>0x0804u});_view.Write(12,selection.IsSystem?1u:0u);_view.Write(16,unchecked((uint)Environment.ProcessId));Thread.MemoryBarrier();_view.Write(20,unchecked((uint)_revision));}
    }
    internal static string MappingName()
    {
        using var user=WindowsIdentity.GetCurrent();using var process=Process.GetCurrentProcess();var family=Family();var hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(family)));
        return $"Local\\HybridMount.UiLanguage.v1.{process.SessionId}.{user.User!.Value}.{hash}";
    }
    private static string Family()
    {
        uint length=0;var code=GetCurrentPackageFamilyName(ref length,null);if(code==15700)return "Unpackaged";if(code!=122||length is 0 or >2048)throw new System.ComponentModel.Win32Exception(code);
        var buffer=new StringBuilder((int)length);code=GetCurrentPackageFamilyName(ref length,buffer);if(code!=0)throw new System.ComponentModel.Win32Exception(code);return buffer.ToString();
    }
    public void Dispose(){lock(_gate){if(_disposed)return;_disposed=true;_view.Dispose();_map.Dispose();}}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern int GetCurrentPackageFamilyName(ref uint length,StringBuilder? buffer);
}
