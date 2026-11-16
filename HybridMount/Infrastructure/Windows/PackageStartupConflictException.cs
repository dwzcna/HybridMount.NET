namespace HybridMount.Infrastructure.Windows;

/// <summary>安装身份冲突不携带旧包路径、数据或认证材料。</summary>
internal sealed class PackageStartupConflictException : IOException { }
