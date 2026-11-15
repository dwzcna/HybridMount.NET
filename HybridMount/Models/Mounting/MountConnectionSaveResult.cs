using HybridMount.Models.UI;
using HybridMount.Commons.UI;
using HybridMount.Models.Localization;
using HybridMount.Models.Mounting;
using HybridMount.Models.Protocols.WebDAV;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
namespace HybridMount.Models.Mounting;

/// <summary>配置保存的实际结果；连接失败不冒充未保存，也不自动重试写入。</summary>
public sealed record MountConnectionSaveResult(MountDefinition Mount, string Message, PageMessageSeverity Severity = PageMessageSeverity.Success,
    [property: System.Text.Json.Serialization.JsonIgnore] HybridMount.Models.Protocols.WebDAV.WebDavCertificateInfo? Certificate = null);
