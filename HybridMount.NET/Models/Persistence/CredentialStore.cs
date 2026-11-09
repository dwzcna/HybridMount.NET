using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using HybridMount.Models.Mounting;

namespace HybridMount.Models.Persistence;

/// <summary>当前 Windows 用户 DPAPI；原子替换密文文件，不依赖明文注册表或跨用户密钥。</summary>
public sealed class CredentialStore(string directory)
{
    private string FileName(Guid id) => Path.Combine(directory, "Credentials", id.ToString("N") + ".bin");
    public void Save(Guid id, SourceCredentials credentials)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(credentials);
        try
        {
            var encrypted = Transform(plain, true);
            var path = FileName(id); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temporary, encrypted); File.Move(temporary, path, true); }
            finally { File.Delete(temporary); }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public SourceCredentials Load(Guid id)
    {
        var path = FileName(id); if (!File.Exists(path)) return new();
        var plain = Transform(File.ReadAllBytes(path), false);
        try { return JsonSerializer.Deserialize<SourceCredentials>(plain) ?? throw new IOException("凭据为空，请重新填写。"); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Delete(Guid id) { var path=FileName(id);if(File.Exists(path))File.Delete(path); }
    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var success = protect ? CryptProtectData(ref input, null, 0, 0, 0, 1, out output)
                : CryptUnprotectData(ref input, 0, 0, 0, 0, 1, out output);
            if (!success) throw new CryptographicException("无法使用当前 Windows 用户保护或读取凭据，请重新填写。");
            var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            for (var i = 0; i < input.Size; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data); if (output.Data != 0) { if(!protect)for(var i=0;i<output.Size;i++)Marshal.WriteByte(output.Data,i,0);LocalFree(output.Data); }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public nint Data; }
    [DllImport("crypt32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input, string? description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input, nint description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}
