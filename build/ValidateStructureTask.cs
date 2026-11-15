#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
#if !HYBRIDMOUNT_HEADLESS_TEST
using Microsoft.Build.Framework;
#endif

namespace HybridMount.BuildTools
{
    /// <summary>四工程共用的只读结构规则；只分析源码、登记和共享路径，不创建运行时服务。</summary>
    internal static class StructureRules
    {
        private static readonly Regex Namespace = new Regex(@"\bnamespace\s+([\w.]+)\s*[;{]");
        // 屏蔽注释和字符串，避免把诊断文案、JSON或测试输入当成代码依赖。
        private static readonly Regex NonCode = new Regex("//[^\\r\\n]*|/\\*[\\s\\S]*?\\*/|@?\"(?:\"\"|\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'");
        internal static string Code(string text) { return NonCode.Replace(text, match => new string(' ', match.Length)); }

        /// <summary>核对目录、命名空间和禁止依赖；唯一命名空间例外是替换生产模拟源的测试桩。</summary>
        internal static IEnumerable<string> CheckSource(string relative, string text)
        {
            relative = relative.Replace('\\', '/');
            var code = Code(text);
            // 注册备份沿用BCL枚举的数字编码；这两个纯值类型不访问注册表。
            if (relative == "HybridMount/Models/Recovery/RecoveryRegistryValue.cs" || relative == "HybridMount/Models/Recovery/RecoveryRegistryBackup.cs") code = code.Replace("using Microsoft.Win32;", "");
            var ns = Namespace.Match(code);
            var expected = relative.Substring(0, relative.LastIndexOf('/')).Replace('/', '.');
            if (relative == "HybridMount.Tests/Integration/Mounting/MockFileSource.cs") expected = "HybridMount.Services.Mounting";
            if (ns.Success && ns.Groups[1].Value != expected) yield return relative + ": namespace 应为 " + expected;
            if (!ns.Success && Regex.IsMatch(code, @"\b(class|record|interface|enum|struct)\s+\w+")) yield return relative + ": 缺少命名空间";
            string forbidden = null;
            if (relative.StartsWith("HybridMount/Models/", StringComparison.Ordinal) || relative.StartsWith("HybridMount/Commons/", StringComparison.Ordinal))
                forbidden = @"HybridMount\.(?:Services|Infrastructure|Composition|UI|Views|Controls)\b|Microsoft\.(?:UI|Data\.Sqlite|Win32)\b|(?:global::)?Windows\.|\b(?:DllImport|LibraryImport)\b";
            else if (relative.StartsWith("HybridMount/Infrastructure/", StringComparison.Ordinal))
                forbidden = @"HybridMount\.(?:Services|Composition|UI|Views|Controls)\b|Microsoft\.UI\b";
            else if (relative.StartsWith("HybridMount/Helpers/", StringComparison.Ordinal))
                forbidden = @"HybridMount\.(?!Helpers\b)[A-Za-z]|Microsoft\.(?:UI|Data|Win32)\b|(?:global::)?Windows\.";
            else if (relative.StartsWith("HybridMount/Services/", StringComparison.Ordinal))
                forbidden = @"HybridMount\.(?:UI|Views|Controls)\b|Microsoft\.UI\b";
            if ((relative.StartsWith("HybridMount/Models/", StringComparison.Ordinal) || relative.StartsWith("HybridMount/Commons/", StringComparison.Ordinal)) && Regex.IsMatch(code, @"\b(?:File|Directory|FileInfo|DirectoryInfo|FileStream|DriveInfo|RegistryKey|Registry|HttpClient|SocketsHttpHandler|SqliteConnection)\s*[.(]"))
                yield return relative + ": 纯数据/契约不能执行系统I/O";
            if (relative.StartsWith("HybridMount/ViewModels/", StringComparison.Ordinal) && Regex.IsMatch(code, @"HybridMount\.(?:Views|Controls)\b|Microsoft\.UI\b|\bApp\.Current\b"))
                yield return relative + ": ViewModel不能引用实际视图或WinUI";
            if (forbidden != null && Regex.IsMatch(code, forbidden)) yield return relative + ": 禁止跨层依赖 " + Regex.Match(code, forbidden).Value;
            if (relative.StartsWith("HybridMount/ViewModels/", StringComparison.Ordinal))
                foreach (Match type in Regex.Matches(code, @"(?m)^public\s+(?:sealed\s+|partial\s+)*(?:class|record)\s+(\w+)"))
                    if (!type.Groups[1].Value.EndsWith("ViewModel", StringComparison.Ordinal)) yield return relative + ": 绑定状态须以ViewModel命名";
        }

        /// <summary>同一partial类型必须集中在一个目录；不根据内部成员名称限制职责拆分。</summary>
        internal static IEnumerable<string> CheckPartials(IEnumerable<KeyValuePair<string, string>> sources)
        {
            var locations = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var source in sources)
            {
                var code = Code(source.Value); var ns = Namespace.Match(code);
                foreach (Match type in Regex.Matches(code, @"\bpartial\s+(?:class|record|struct)\s+(\w+)"))
                {
                    var key = ns.Groups[1].Value + "." + type.Groups[1].Value;
                    var directory = Path.GetDirectoryName(source.Key).Replace('\\', '/');
                    string previous;
                    if (locations.TryGetValue(key, out previous) && previous != directory) yield return key + ": partial分散在 " + previous + " 和 " + directory;
                    locations[key] = directory;
                }
            }
        }
    }

#if !HYBRIDMOUNT_HEADLESS_TEST
    /// <summary>构建前核对实际Compile集合、命名空间、依赖、XAML及原生共享登记；失败阻止编译。</summary>
    public sealed class HybridMountValidateStructure : Microsoft.Build.Utilities.Task
    {
        [Required] public string RepositoryRoot { get; set; }
        public string ProjectDirectory { get; set; }
        public ITaskItem[] ManagedSources { get; set; }
        public override bool Execute()
        {
            try
            {
                var root = Path.GetFullPath(RepositoryRoot);
                var sources = new List<KeyValuePair<string, string>>();
                foreach (var folder in new[] { "HybridMount", "HybridMount.Tests" })
                    foreach (var path in Files(Path.Combine(root, folder), "*.cs"))
                    {
                        var relative = Relative(root, path);
                        var text = File.ReadAllText(path);
                        sources.Add(new KeyValuePair<string, string>(relative, text));
                        foreach (var error in StructureRules.CheckSource(relative, text)) Log.LogError(error);
                    }
                foreach (var error in StructureRules.CheckPartials(sources)) Log.LogError(error);
                CheckManaged(root);
                foreach (var name in new[] { "HybridMount.Ext", "HybridMount.Stock" }) CheckNative(root, name);
                CheckXaml(root);
                if (!Log.HasLoggedErrors) Log.LogMessage(MessageImportance.High, "Structure checked: {0} C# sources / managed structure / baseline native registration", sources.Count);
                return !Log.HasLoggedErrors;
            }
            catch (Exception error) { Log.LogError("结构检查失败：{0}", error.Message); return false; }
        }
        private static IEnumerable<string> Files(string directory, string pattern)
        {
            if (!Directory.Exists(directory)) yield break;
            foreach (var file in Directory.EnumerateFiles(directory, pattern)) yield return file;
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(child);
                if (name.Equals("obj", StringComparison.OrdinalIgnoreCase) || name.Equals("bin", StringComparison.OrdinalIgnoreCase) || name.Equals("ThirdParty", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var file in Files(child, pattern)) yield return file;
            }
        }
        private static string Relative(string root, string path) { return Path.GetFullPath(path).Substring(root.TrimEnd('\\', '/').Length + 1).Replace('\\', '/'); }
        private void CheckManaged(string root)
        {
            if (ManagedSources == null || ManagedSources.Length == 0 || string.IsNullOrEmpty(ProjectDirectory)) return;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in ManagedSources)
            {
                var path = item.GetMetadata("FullPath");
                if (path.IndexOf("\\obj\\", StringComparison.OrdinalIgnoreCase) >= 0 || path.IndexOf("/obj/", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (!File.Exists(path)) Log.LogError("Compile不存在：{0}", path);
                if (!seen.Add(path)) Log.LogError("Compile重复：{0}", path);
            }
            var project = Path.GetFullPath(ProjectDirectory).TrimEnd('\\', '/');
            foreach (var path in Files(project, "*.cs")) if (!seen.Contains(path)) Log.LogError("源码未登记Compile：{0}", Relative(root, path));
            if (Path.GetFileName(project) == "HybridMount.Tests")
            {
                var props = new XmlDocument(); props.Load(Path.Combine(project, "ProductionSources.props"));
                foreach (XmlElement item in props.SelectNodes("/Project/ItemGroup/Compile"))
                {
                    var includes = Expand(project, item.GetAttribute("Include"));
                    var excludes = new HashSet<string>(item.GetAttribute("Exclude").Split(';').Where(x => x.Length != 0).SelectMany(x => Expand(project, x)), StringComparer.OrdinalIgnoreCase);
                    foreach (var path in includes) if (!excludes.Contains(path) && !seen.Contains(path)) Log.LogError("生产链接遗漏：{0}", Relative(root, path));
                }
            }
        }
        private static IEnumerable<string> Expand(string project, string input)
        {
            input = input.Replace('\\', '/');
            if (!input.Contains("*")) return new[] { Path.GetFullPath(Path.Combine(project, input)) };
            var start = input.IndexOf('*'); var prefix = input.Substring(0, input.LastIndexOf('/', start) + 1);
            var folder = Path.GetFullPath(Path.Combine(project, prefix));
            return input.IndexOf("**", StringComparison.Ordinal) >= 0 ? Files(folder, "*.cs") : Directory.EnumerateFiles(folder, "*.cs");
        }
        private void CheckNative(string root, string name)
        {
            var directory = Path.Combine(root, name); var project = new XmlDocument(); project.Load(Path.Combine(directory, name + ".vcxproj"));
            var compiled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XmlElement item in project.SelectNodes("//*[local-name()='ClCompile' or local-name()='ClInclude' or local-name()='ResourceCompile'][@Include]"))
            {
                var path = Path.GetFullPath(Path.Combine(directory, item.GetAttribute("Include")));
                if (!File.Exists(path)) Log.LogError("原生登记不存在：{0}", path);
                if (item.LocalName == "ClCompile" && !compiled.Add(path)) Log.LogError("原生Compile重复：{0}", path);
            }
            foreach (var source in Files(directory, "*.cpp")) if (!compiled.Contains(source)) Log.LogError("原生源码未登记：{0}", source);
            foreach (var shared in new[] { "NativeLanguage.cpp", "ThumbnailCache.cpp" })
                if (!compiled.Contains(Path.GetFullPath(Path.Combine(root, "HybridMount.Stock", shared)))) Log.LogError("{0}未编译共享源：{1}", name, shared);
            if (!File.Exists(Path.Combine(root, "HybridMount.Stock", "ThirdParty", "LICENSE.txt"))) Log.LogError("共享第三方版权缺失");
        }
        private void CheckXaml(string root)
        {
            foreach (var path in Files(Path.Combine(root, "HybridMount"), "*.xaml"))
            {
                var document = new XmlDocument(); document.Load(path);
                var type = document.DocumentElement.GetAttribute("Class", "http://schemas.microsoft.com/winfx/2006/xaml");
                if (type.Length == 0) continue;
                var codeFile = path + ".cs";
                if (!File.Exists(codeFile)) { Log.LogError("XAML缺少代码：{0}", Relative(root, path)); continue; }
                var expected = Path.GetFileNameWithoutExtension(path);
                var code = StructureRules.Code(File.ReadAllText(codeFile));
                if (!Regex.IsMatch(code, @"\bpartial\s+class\s+" + Regex.Escape(expected) + @"\b") || type != Relative(root, Path.GetDirectoryName(path)).Replace('/', '.') + "." + expected)
                    Log.LogError("XAML类型/目录不一致：{0}", Relative(root, path));
            }
        }
    }
#endif
}
