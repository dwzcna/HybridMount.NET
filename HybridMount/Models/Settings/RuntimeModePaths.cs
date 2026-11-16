namespace HybridMount.Models.Settings;

/// <summary>仅组合已确认的运行模式标识，不读取注册表或系统目录。</summary>
internal static class RuntimeModePaths
{
    internal static string RegistryPath(bool packaged)=>@"Software\"+ProductIdentity.PhysicalDirectoryName(packaged)+"\\"+(packaged?"Packaged":"Unpackaged");
}
