using HybridMount.Models.Recovery;
using HybridMount.Models.Mounting;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Globalization;
namespace HybridMount.Commons.Windows;

/// <summary>隔离系统注册访问边界；核验返回精确残项，旧测试适配器仍可保持原调用。</summary>
internal interface IRecoveryShellRegistration
{
    IReadOnlyList<RecoveryRegistration> List();
    RecoveryShellProof? Capture(OrphanedMountCandidate root);
    void Unregister(string id);
    void Cleanup(OrphanedMountCandidate root,RecoveryShellProof? proof,Action verifyContent);
    string[] Verify(OrphanedMountCandidate root,RecoveryShellProof? proof)=>[];
}
