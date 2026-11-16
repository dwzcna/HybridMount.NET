#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace HybridMount.BuildTools
{
    /// <summary>只读计算实际文件集合和内容摘要；路径也参与认证，以发现新增、删除与改名。</summary>
    public static class BuildFingerprint
    {
        public static string Text(string value)
        {
            using (var hash = SHA256.Create()) return Hex(hash.ComputeHash(Encoding.UTF8.GetBytes(value)));
        }
        public static string Files(IEnumerable<string> paths, string context) => Files(paths, context, null);
        public static string Files(IEnumerable<string> paths, string context, Action<string, double>? measured)
        {
            var summary = new StringBuilder(context);
            foreach (var path in paths.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal))
            {
                summary.Append('\n').Append(path.Replace('\\', '/')).Append('\0');
                if (!File.Exists(path)) { summary.Append("missing"); continue; }
                var watch = measured == null ? null : System.Diagnostics.Stopwatch.StartNew();
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var hash = SHA256.Create()) summary.Append(stream.Length).Append(':').Append(Hex(hash.ComputeHash(stream)));
                if (measured != null && watch != null) measured(path, watch.Elapsed.TotalSeconds);
            }
            return Text(summary.ToString());
        }
        public static string[] Tree(string directory)
        {
            var paths = new List<string>();
            if (Directory.Exists(directory)) Walk(directory, paths, false);
            return paths.ToArray();
        }
        public static string[] Sources(string root)
        {
            var paths = new List<string>();
            foreach (var folder in new[] { "HybridMount", "HybridMount.Tests", "HybridMount.Ext", "HybridMount.Stock", "build" })
                if (Directory.Exists(Path.Combine(root, folder))) Walk(Path.Combine(root, folder), paths, true);
            foreach (var path in Directory.EnumerateFiles(root))
                if (new[] { ".props", ".targets", ".slnx", ".json", ".config" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) paths.Add(path);
            return paths.ToArray();
        }
        private static void Walk(string folder, List<string> result, bool source)
        {
            result.AddRange(Directory.EnumerateFiles(folder));
            foreach (var child in Directory.EnumerateDirectories(folder))
            {
                var name = Path.GetFileName(child);
                if (source && new[] { "bin", "obj", ".git", ".vs", "artifacts", "TestResults" }.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                // 输入目录不能经重解析点把缓存范围扩展到工作区外；发现后让完整构建负责处理。
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) throw new IOException("构建摘要不跟随重解析目录：" + child);
                Walk(child, result, source);
            }
        }
        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    }
}

#nullable disable
// PowerShell仅需要BCL摘要；MSBuild编译同一份源时附带任务适配。
#if !HYBRIDMOUNT_FINGERPRINT_ONLY
namespace HybridMount.BuildTools
{
    /// <summary>门禁的内容摘要包括文件集合和每个工程实际Compile登记；失败不写入成功戳。</summary>
    public sealed class ValidationFingerprintTask : Microsoft.Build.Utilities.Task
    {
        [Microsoft.Build.Framework.Required] public string RepositoryRoot { get; set; }
        public string ProjectDirectory { get; set; }
        [Microsoft.Build.Framework.Required] public string StampFile { get; set; }
        public Microsoft.Build.Framework.ITaskItem[] ManagedSources { get; set; }
        [Microsoft.Build.Framework.Output] public string Fingerprint { get; set; }
        [Microsoft.Build.Framework.Output] public bool Reusable { get; set; }
        public override bool Execute()
        {
            try
            {
                var context = new System.Text.StringBuilder("validation-v1:" + ProjectDirectory);
                foreach (var item in (ManagedSources ?? new Microsoft.Build.Framework.ITaskItem[0]).OrderBy(item => item.ItemSpec, System.StringComparer.Ordinal))
                {
                    var path = item.GetMetadata("FullPath").Replace('\\', '/');
                    if (path.IndexOf("/obj/", System.StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    context.Append('\n').Append(path).Append('|').Append(item.GetMetadata("Link"));
                }
                Fingerprint = BuildFingerprint.Files(BuildFingerprint.Sources(System.IO.Path.GetFullPath(RepositoryRoot)), context.ToString());
                Reusable = System.IO.File.Exists(StampFile) && System.IO.File.ReadAllText(StampFile).Trim() == Fingerprint;
                return true;
            }
            catch (System.Exception error)
            {
                // 无法证明一致时保留官方门禁，不把缓存故障升级为跳过校验。
                Reusable = false; Fingerprint = "";
                Log.LogMessage(Microsoft.Build.Framework.MessageImportance.Normal, "Validation cache unavailable: {0}", error.Message);
                return true;
            }
        }
    }
}

#endif
