namespace HybridMount.Models.Mounting;

/// <summary>定义的持久字段校验，不读取或改写任何配置；读取失败不能触发默认值覆盖。</summary>
public static class MountDefinitionRules
{
    public const int RegistrySchemaVersion=10;
    public static void Validate(MountDefinition definition,string registryName)
    {
        CapacityRules.Validate(definition.Source?.Kind,definition.CapacityAutoDetectEnabled,definition.MaximumCapacityBytes);
        if (!Enum.IsDefined(definition.RegistrationStage) || !Enum.IsDefined(definition.RemovalMode) || !Enum.IsDefined(definition.RemovalStage) || definition.RemoteIncrementalSeconds is <10 or >86400 || definition.RemoteFullSeconds is <30 or >604800 || definition.CredentialRevision<0 || definition.Id == Guid.Empty || definition.Id.ToString("N") != registryName || !Path.IsPathFullyQualified(definition.LocalPath)) throw new IOException("挂载配置无效。");
    }
}
