using HybridMount.BuildTools;
namespace HybridMount.Tests.Architecture;

/// <summary>验证构建门禁拒绝跨层引用和漂移，同时允许纯数据、注释和专用测试桩。</summary>
public sealed class SourceStructureTests
{
    [Theory]
    [InlineData("HybridMount/Models/Case.cs", "namespace HybridMount.Models; using HybridMount.Infrastructure.Files; public class Case {}")]
    [InlineData("HybridMount/Commons/Case.cs", "namespace HybridMount.Commons; using Microsoft.UI.Xaml; public interface Case {}")]
    [InlineData("HybridMount/Infrastructure/Case.cs", "namespace HybridMount.Infrastructure; using HybridMount.Services.Sync; class Case {}")]
    [InlineData("HybridMount/Helpers/Case.cs", "namespace HybridMount.Helpers; using HybridMount.Models; class Case {}")]
    [InlineData("HybridMount/Services/Case.cs", "namespace HybridMount.Services; using HybridMount.UI; class Case {}")]
    [InlineData("HybridMount/ViewModels/Case.cs", "namespace Wrong.Place; public class Case {}")]
    [InlineData("HybridMount/Models/Case.cs", "namespace HybridMount.Models; class Case { string Read() => System.IO.File.ReadAllText(\"x\"); }")]
    [InlineData("HybridMount/Models/Recovery/RecoveryRegistryValue.cs", "namespace HybridMount.Models.Recovery; using Microsoft.Win32; class Case { void Read() { RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64); } }")]
    [InlineData("HybridMount/ViewModels/CaseViewModel.cs", "namespace HybridMount.ViewModels; using Microsoft.UI.Xaml; public class CaseViewModel {}")]
    public void ForbiddenDependencyOrNamespaceIsRejected(string path, string code) => Assert.NotEmpty(StructureRules.CheckSource(path, code));

    [Fact]
    public void CommentsAndMessagesAreNotDependencies()
    {
        const string code = "namespace HybridMount.Models; // HybridMount.Infrastructure.Files\n public record Case(string Name) { string Text => \"HybridMount.UI.View\"; }";
        Assert.Empty(StructureRules.CheckSource("HybridMount/Models/Case.cs", code));
        Assert.Empty(StructureRules.CheckSource("HybridMount.Tests/Integration/Mounting/MockFileSource.cs", "namespace HybridMount.Services.Mounting; class MockFileSource {}"));
    }

    [Fact]
    public void PartialTypeCannotSpanDirectories()
    {
        var sources = new[]
        {
            KeyValuePair.Create("HybridMount/Services/One/A.cs", "namespace HybridMount.Services; partial class Shared {}"),
            KeyValuePair.Create("HybridMount/Services/Two/B.cs", "namespace HybridMount.Services; partial class Shared {}")
        };
        Assert.Single(StructureRules.CheckPartials(sources));
    }
}
