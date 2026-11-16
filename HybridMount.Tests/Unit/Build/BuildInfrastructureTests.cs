using HybridMount.BuildTools;
using System.Diagnostics;
using System.Text.Json;
namespace HybridMount.Tests.Unit.Build;

/// <summary>验证构建复用依赖实际内容和集合，不以时间戳或成功文件存在代替输入／输出保护。</summary>
public sealed class BuildInfrastructureTests
{
    [Theory]
    [InlineData("add")]
    [InlineData("delete")]
    [InlineData("rename")]
    [InlineData("content")]
    public void Source_changes_invalidate_even_with_preserved_timestamp(string change)
    {
        WithArea(area =>
        {
            var folder=Path.Combine(area,"HybridMount");Directory.CreateDirectory(folder);
            var file=Path.Combine(folder,"One.cs");File.WriteAllText(file,"same");var stamp=File.GetLastWriteTimeUtc(file);
            var before=BuildFingerprint.Files(BuildFingerprint.Sources(area),"inputs");
            switch(change)
            {
                case "add": File.WriteAllText(Path.Combine(folder,"Two.cs"),"same");break;
                case "delete": File.Delete(file);break;
                case "rename": File.Move(file,Path.Combine(folder,"Two.cs"));break;
                case "content": File.WriteAllText(file,"else");File.SetLastWriteTimeUtc(file,stamp);break;
            }
            Assert.NotEqual(before,BuildFingerprint.Files(BuildFingerprint.Sources(area),"inputs"));
        });
    }
    [Theory]
    [InlineData("x64","ARM64")]
    [InlineData("Full","Small")]
    [InlineData("Packaged","Unpackaged")]
    [InlineData("Debug","Release")]
    [InlineData("Menus","MenusAndThumbnails")]
    [InlineData("FullFixtures","NoFixtures")]
    public void Build_properties_are_part_of_the_digest(string before,string after)
    {
        WithArea(area =>
        {
            var path=Path.Combine(area,"input");File.WriteAllText(path,"unchanged");
            Assert.NotEqual(BuildFingerprint.Files([path],before),BuildFingerprint.Files([path],after));
        });
    }
    [Fact]
    public void Generated_app_packages_do_not_become_source_inputs()
    {
        WithArea(area =>
        {
            var source = Path.Combine(area, "HybridMount"); Directory.CreateDirectory(source);
            var file = Path.Combine(source, "App.cs"); File.WriteAllText(file, "source");
            var before = BuildFingerprint.Files(BuildFingerprint.Sources(area), "inputs");
            var packages = Path.Combine(source, "AppPackages"); Directory.CreateDirectory(packages);
            File.WriteAllText(Path.Combine(packages, "previous.msix"), "generated");
            Assert.Equal(before, BuildFingerprint.Files(BuildFingerprint.Sources(area), "inputs"));
            File.WriteAllText(file, "changed");
            Assert.NotEqual(before, BuildFingerprint.Files(BuildFingerprint.Sources(area), "inputs"));
        });
    }
    [Fact]
    public void Missing_input_and_output_set_changes_are_detected()
    {
        WithArea(area =>
        {
            var file=Path.Combine(area,"required.dll");var missing=BuildFingerprint.Files([file],"inputs");
            File.WriteAllText(file,"payload");Assert.NotEqual(missing,BuildFingerprint.Files([file],"inputs"));
            var before=BuildFingerprint.Files(BuildFingerprint.Tree(area),"outputs");
            File.WriteAllText(Path.Combine(area,"extra.dll"),"payload");Assert.NotEqual(before,BuildFingerprint.Files(BuildFingerprint.Tree(area),"outputs"));
        });
    }
    [Fact]
    public async Task PowerShell_success_records_and_fixture_selection_obey_the_same_rules()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root is not null&&!File.Exists(Path.Combine(root.FullName,"build","Test-BuildCache.ps1")))root=root.Parent;
        Assert.NotNull(root);
        var area=Directory.CreateTempSubdirectory("HybridMount.BuildCache.");
        try
        {
            var start=new ProcessStartInfo("pwsh"){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var value in new[]{"-NoProfile","-File",Path.Combine(root.FullName,"build","Test-BuildCache.ps1"),"-WorkDirectory",area.FullName})start.ArgumentList.Add(value);
            using var process=Process.Start(start)!;var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));await process.WaitForExitAsync(timeout.Token);
            var text=await output;Assert.True(process.ExitCode==0,text+await error);
            using var report=JsonDocument.Parse(text);Assert.True(report.RootElement.GetProperty("Passed").GetInt32()>=14);
        }
        finally{area.Delete(true);}
    }
    [Fact]
    public async Task Store_submission_requires_legal_version_and_real_materials()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "build", "Test-StorePolicy.ps1"))) root = root.Parent;
        Assert.NotNull(root);
        var area = Directory.CreateTempSubdirectory("HybridMount.StorePolicy.");
        try
        {
            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var value in new[] { "-NoProfile", "-File", Path.Combine(root.FullName, "build", "Test-StorePolicy.ps1"), "-WorkDirectory", area.FullName }) start.ArgumentList.Add(value);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45)); await process.WaitForExitAsync(timeout.Token);
            var text = await output; Assert.True(process.ExitCode == 0, text + await error);
            using var report = JsonDocument.Parse(text); Assert.True(report.RootElement.GetProperty("Passed").GetInt32() >= 25);
        }
        finally { area.Delete(true); }
    }
    private static void WithArea(Action<string> action)
    {
        var area=Directory.CreateTempSubdirectory("HybridMount.Fingerprint.");
        try{action(area.FullName);}finally{area.Delete(true);}
    }
}
