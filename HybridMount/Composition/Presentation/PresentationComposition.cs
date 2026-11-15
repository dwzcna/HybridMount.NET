using System.Runtime.CompilerServices;
using HybridMount.Commons.Presentation;
using HybridMount.UI.Presentation;
namespace HybridMount.Composition.Presentation;

/// <summary>只装配兼容投影，无文件、注册、控件或网络访问；在任何历史／IPC序列化前完成。</summary>
internal static class PresentationComposition
{
    [ModuleInitializer]
    internal static void Initialize()=>ModelPresentation.Configure(new ModelPresentationRenderer());
}
