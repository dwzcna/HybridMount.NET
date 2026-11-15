namespace HybridMount.Models.Mounting;

/// <summary>领域路径入口沿用相同纯路径算法，不访问文件系统。</summary>
public static class RelativePathRules
{
    public static string Normalize(string path) => Helpers.Paths.RelativePaths.Normalize(path);
    public static string Parent(string path) => Helpers.Paths.RelativePaths.Parent(path);
    public static bool Contains(string parent, string path) => Helpers.Paths.RelativePaths.Contains(parent, path);
    public static string ToLocal(string root, string relative) => Helpers.Paths.RelativePaths.ToLocal(root, relative);
}
