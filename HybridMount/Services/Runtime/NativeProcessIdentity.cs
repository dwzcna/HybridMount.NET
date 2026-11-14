using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace HybridMount.Services.Runtime;

/// <summary>挂起子进程的实际身份快照，不包含启动参数、管道名或认证材料。</summary>
internal sealed record NativeChildIdentity(string ExpectedPackage, string ActualPackage, string ExpectedImage, string ActualImage,
    string ExpectedUser, string ActualUser, int ExpectedSession, int ActualSession)
{
    internal bool Matches => !string.IsNullOrEmpty(ExpectedImage) && !string.IsNullOrEmpty(ExpectedUser) && ExpectedPackage == ActualPackage && string.Equals(ExpectedImage, ActualImage, StringComparison.OrdinalIgnoreCase) &&
        ExpectedUser == ActualUser && ExpectedSession == ActualSession;
}
/// <summary>创建者在加入Job后、恢复主线程前查询内核身份；任何查询错误均拒绝启动，不以空值冒充非打包。</summary>
internal static class NativeProcessIdentity
{
    internal static bool IsPackaged => Package(-1).Length != 0;
    internal static NativeChildIdentity Verify(Process child, string expectedImage, string role)
    {
        using var current = Process.GetCurrentProcess();
        Check(OpenProcessToken(child.Handle, 8, out var token));
        using (token)
        using (var user = new WindowsIdentity(token.DangerousGetHandle()))
        using (var expectedUser = WindowsIdentity.GetCurrent())
        {
            var image = new StringBuilder(32768); uint size = (uint)image.Capacity;
            Check(QueryFullProcessImageName(child.Handle, 0, image, ref size));
            var proof = new NativeChildIdentity(Package(current.Handle), Package(child.Handle), Path.GetFullPath(expectedImage), image.ToString(),
                expectedUser.User?.Value ?? throw new IOException("无法核验父进程账户。"), user.User?.Value ?? throw new IOException("无法核验子进程账户。"),
                current.SessionId, child.SessionId);
            if (role == "NativeRuntime" || !proof.Matches) HostDiagnosticStore.RecordChildIdentity(role, proof);
            Validate(proof);
            return proof;
        }
    }
    internal static void Validate(NativeChildIdentity proof)
    {
        if (!proof.Matches) throw new IOException("原生启动身份核验失败（包、映像、用户或会话不一致），已保留父进程诊断。");
    }
    private static string Package(nint process)
    {
        uint size = 0; var status = GetPackageFullName(process, ref size, null);
        if (status == 15700) return "";
        if (status != 122 || size is < 2 or > 2048) throw new Win32Exception(status);
        var value = new StringBuilder((int)size); status = GetPackageFullName(process, ref size, value);
        if (status != 0) throw new Win32Exception(status);
        return value.ToString();
    }
    private static void Check(bool value) { if (!value) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] private static extern int GetPackageFullName(nint process, ref uint size, StringBuilder? value);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder value, ref uint size);
    [DllImport("advapi32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);
}
