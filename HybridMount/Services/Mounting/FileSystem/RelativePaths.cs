using HybridMount.Models.Mounting;
namespace HybridMount.Services.Mounting;

/// <summary>既有公开入口的兼容适配；所有路径校验和映射算法归领域模型，调用不触发 IO。</summary>
public static class RelativePaths
{
    public static string Normalize(string path)=>RelativePathRules.Normalize(path);
    public static string Parent(string path)=>RelativePathRules.Parent(path);
    public static bool Contains(string parent,string path)=>RelativePathRules.Contains(parent,path);
    public static string ToLocal(string root,string relative)=>RelativePathRules.ToLocal(root,relative);
}
