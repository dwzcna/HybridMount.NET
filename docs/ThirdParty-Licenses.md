# 第三方许可核对（0.0.25.2）

本项目原创代码和文档按根目录 MIT LICENSE 分发，作者 dwzcn（2026）。RyleD 是包发布者，不替代作者。第三方目录、NuGet 组件、运行时、原生依赖和系统资源继续按各自条款授权；整个 MSIX／Full 目录不因此变为只有 MIT 一种许可。

## 实际核对范围

核对主程序及测试工程实际 project.assets.json、解包后的 nuspec、nupkg 中的原文和实际二进制；Ext／Stock 的原生内置源码、/MT 运行库及 SDK 引用也纳入。准确还原版本如下；“开发”仅表示不随应用分发工具二进制。原始文稿沿用 HybridMount/ThirdParty/ 这一份集合，详细来源见 Provenance.txt，文件摘要和上游下载证据在 artifacts/validation/00252-work/。

| 已还原组件／版本 | 范围 | 原许可证据 |
| --- | --- | --- |
| AWSSDK.Core/4.0.102.14 | 应用依赖（传递） | Apache-2.0 |
| AWSSDK.S3/4.0.104.7 | 应用依赖（直接） | Apache-2.0 |
| AWSSDK.SQS/4.0.100.21 | 应用依赖（直接） | Apache-2.0 |
| CommunityToolkit.Common/8.2.1 | 应用依赖（传递） | MIT |
| CommunityToolkit.Mvvm/8.4.0 | 应用依赖（直接） | MIT |
| CommunityToolkit.WinUI.Controls.Segmented/8.2.251219 | 应用依赖（直接） | MIT |
| CommunityToolkit.WinUI.Extensions/8.2.251219 | 应用依赖（传递） | MIT |
| Microsoft.Data.Sqlite/10.0.12 | 应用依赖（直接） | MIT |
| Microsoft.Data.Sqlite.Core/10.0.12 | 应用依赖（传递） | MIT |
| Microsoft.Web.WebView2/1.0.3719.77 | 应用依赖（传递） | 包内 LICENSE.txt |
| Microsoft.Windows.AI.MachineLearning/2.1.74 | 应用依赖（传递） | 包内 license.txt |
| Microsoft.Windows.SDK.BuildTools/10.0.28000.2705 | 开发／构建（直接） | https://aka.ms/WinSDKLicenseURL |
| Microsoft.Windows.SDK.BuildTools.MSIX/1.7.251221100 | 开发／构建（传递） | 包内 sdk_license.txt |
| Microsoft.WindowsAppSDK/2.5.1 | 应用依赖（直接） | 包内 license.txt |
| Microsoft.WindowsAppSDK.AI/2.5.5 | 应用依赖（传递） | 包内 license.txt |
| Microsoft.WindowsAppSDK.Base/2.0.4 | 应用依赖（传递） | 包内 license.txt |
| Microsoft.WindowsAppSDK.DWrite/2.1.0 | 应用依赖（传递） | 包内 license.txt |
| Microsoft.WindowsAppSDK.Foundation/2.3.12 | 应用依赖（传递） | 包内 license.txt |
| Microsoft.WindowsAppSDK.InteractiveExperiences/2.1.9 | 应用依赖（传递） | 包内 license.txt |
| Microsoft.WindowsAppSDK.ML/2.1.94 | 应用依赖（传递） | 包内 license.txt |
| Microsoft.WindowsAppSDK.Runtime/2.5.1 | 应用依赖（传递） | 包内 license.txt |
| Microsoft.WindowsAppSDK.Search/2.5.5 | 应用依赖（传递） | 包内 license.txt |
| Microsoft.WindowsAppSDK.Widgets/2.0.5 | 应用依赖（传递） | 包内 license.txt |
| Microsoft.WindowsAppSDK.WinUI/2.3.9 | 应用依赖（传递） | 包内 license.txt |
| SQLitePCLRaw.bundle_e_sqlite3/2.1.12 | 应用依赖（传递） | Apache-2.0 |
| SQLitePCLRaw.core/2.1.12 | 应用依赖（传递） | Apache-2.0 |
| SQLitePCLRaw.lib.e_sqlite3/2.1.12 | 应用依赖（传递） | Apache-2.0 |
| SQLitePCLRaw.provider.e_sqlite3/2.1.12 | 应用依赖（传递） | Apache-2.0 |
| System.Numerics.Tensors/9.0.0 | 应用依赖（传递） | MIT |
| WebDAVClient/3.3.0 | 应用依赖（直接） | MIT |
| Microsoft.CodeCoverage/18.0.1 | 开发／测试 | MIT |
| Microsoft.NET.Test.Sdk/18.0.1 | 开发／测试 | MIT |
| Microsoft.TestPlatform.ObjectModel/18.0.1 | 开发／测试 | MIT |
| Microsoft.TestPlatform.TestHost/18.0.1 | 开发／测试 | MIT |
| Newtonsoft.Json/13.0.3 | 开发／测试 | MIT |
| xunit/2.9.3 | 开发／测试 | Apache-2.0 |
| xunit.abstractions/2.0.3 | 开发／测试 | Apache-2.0；原 nuspec 指向 xUnit 官方许可 |
| xunit.analyzers/1.18.0 | 开发／测试 | Apache-2.0 |
| xunit.assert/2.9.3 | 开发／测试 | Apache-2.0 |
| xunit.core/2.9.3 | 开发／测试 | Apache-2.0 |
| xunit.extensibility.core/2.9.3 | 开发／测试 | Apache-2.0 |
| xunit.extensibility.execution/2.9.3 | 开发／测试 | Apache-2.0 |
| xunit.runner.visualstudio/3.1.5 | 开发／测试 | Apache-2.0 |

## 组合交付的条件

| 组件 | 当前核对与条件 |
| --- | --- |
| 原创代码／文档 | MIT，保留 dwzcn 2026 版权和完整许可；不重新许可下列组件。 |
| WebDAVClient 3.3.0 | 当前包为 MIT；原 LICENSE 和 provenance NOTICE 原样保留。NOTICE 中的历史 CC0 和归档 MIT／Apache 记录，不是当前整包双许可选择。 |
| AWS Core 4.0.102.14／S3 4.0.104.7／SQS 4.0.100.21 | nuspec 明确 Apache-2.0；保留完整条款、原公共 NOTICE，补充官方固定提交 80697dc1074776fd534d331cfa817f6741b32556 的原文，并注明它是上游快照而非 nupkg 内文件。专利许可、专利诉讼终止、NOTICE 和商标限制继续适用。 |
| nlohmann/json 3.12.0 | 内置头文件 MIT；Stock/ThirdParty 和随包 ThirdParty/nlohmann 许可保持。未添加其他原生解析库。 |
| Microsoft.Data.Sqlite／Core 10.0.12 | 托管适配器 MIT；不能据此把原生 SQLite 构建、SQLitePCLRaw 或所有 SDK 二进制一并标为 MIT。 |
| SQLitePCLRaw bundle／core／provider 2.1.12 | Apache-2.0，保留 SourceGear 归属及 v2.1.12 原始 NOTICE。 |
| SQLitePCLRaw.lib.e_sqlite3 2.1.12 与 SQLite 核心 | 原生包声明 Apache-2.0；SQLite 核心属于公有领域。实际 DLL 报告 SQLite **3.53.3**，不是从包装版本推断；双架构 DLL 与各自 nupkg 条目字节一致，含同一 source ID（见下）。不根据聚合 NOTICE 中的可选组件名推断实际链接 SQLCipher 等组件。 |
| .NET 10.0.12／apphost | 两架构 runtime 包原始 LICENSE.TXT、THIRD-PARTY-NOTICES.TXT 随交付保留；Full 包含运行时，Small 外置共享运行时但仍带 apphost。SDK 10.0.401 是开发工具，不整套再分发。 |
| Windows App SDK／Windows App Runtime／WinUI | 包内 Microsoft Software License Terms 是这些二进制的分发依据，不能用其源码项目的 MIT 声明代替。允许 NuGet 随 Windows 应用 binplace 的文件再分发，但要求主要应用功能、让下游接受至少同等保护 Microsoft 的条款、承担协议规定的分发责任，并保留原始声明及商标限制；适用第三方例外仍有效。Full 带运行时；Small 仍有托管／引导文件，框架运行时作为前置条件。 |
| Eigen／MPL-2.0 与 SDK 聚合 NOTICE | 实际微软 NOTICE 包含 Eigen 的 MPL-2.0 和源码获取途径，均原样保留；未修改该第三方源码。MPL “Secondary License” 定义里的 GNU 名称及通用 LGPL 逆向调试例外，不是将 HybridMount 整体指定为 GPL 的证据。覆盖文件和源获取义务仍由其原许可决定。 |
| WebView2 SDK 1.0.3719.77 | 包内 LICENSE.txt 为 Microsoft 版权的 BSD 三条款，NOTICE 同时保留。该结论仅针对本 SDK 文稿，不给另行安装的 WebView2 Runtime 授予 MIT／BSD 再许可；Runtime 按自己的微软条款。 |
| MSVC 14.51.36231 原生 /MT | Ext／Stock 使用静态 C++ 运行库对象，依 Visual Studio 的可分发代码条款授权；不是本项目 MIT 的一部分。仅分发应用，不附带整个 VS／编译器。官方再分发清单和 V14 Runtime 条款见来源。 |
| Windows SDK 10.0.26100.0、BuildTools 10.0.28000.2705／MSIX 工具 1.7.251221100 | SDK 头／导入库和构建工具仅用于开发，应用不分发 makeappx、makepri、MSBuild 或 VS。原 MSIX 工具文稿留作来源。 |
| 系统 CFAPI／Win32／UIA／Media Foundation／图标字体 | 系统接口和已安装 Segoe 字体是 Windows 前置条件，不复制系统 DLL 或字体文件到仓库／应用。自有品牌资源未因 Publisher 更名而改作者。 |
| xUnit／Test SDK／TestPlatform／Newtonsoft 测试依赖 | 上表列准确版本与声明。xUnit 2.0.3 标签和实际包均指向 xUnit 官方许可，官方 v2-2.4.1／v2-2.9.3 文稿记录 Apache-2.0 及导入代码例外；测试框架和测试宿主不随应用二进制分发。 |

结论：原创源码可按 MIT 发布；当前组合二进制可按各组件的分发条件一并交付，不能只保留根 MIT 文件而移除第三方条款。没有将现有许可证文本中的关键词扫描结果当作整个应用许可结论，也没有删除功能来回避条款。

## 原文与来源

- [根 MIT LICENSE](../LICENSE)；[准确依赖清单](../HybridMount/ThirdParty/Dependencies.txt)；[随包来源说明](../HybridMount/ThirdParty/Provenance.txt)。
- [AWS 固定上游提交](https://github.com/aws/aws-sdk-net/tree/80697dc1074776fd534d331cfa817f6741b32556)。
- [SQLitePCLRaw v2.1.12 NOTICE](https://github.com/ericsink/SQLitePCL.raw/blob/v2.1.12/NOTICE.TXT)；[SQLite 公有领域说明](https://www.sqlite.org/copyright.html)。
- [Visual Studio 2026 可分发清单](https://learn.microsoft.com/en-us/visualstudio/releases/2026/redistribution)；[V14 Runtime 2026 条款](https://visualstudio.microsoft.com/license-terms/vs2026-ga-visualcpp-v14-redist-runtime/)。
- Windows App SDK／WebView2 的准据为实际包内文件，不以当前网页或上游源码许可替换原包原文。

实际 SQLite source ID：`2026-06-26 20:14:12 d4c0e51e4aeb96955b99185ab9cde75c339e2c29c3f3f12428d364a10d782c62`。

## 交付核验

仓库使用.gitattributes为第三方原文关闭Git换行转换，保留上游空格和换行；原文不作格式清理。包检查与目录／ZIP检查复用一套许可文件集合，逐个比对仓库原文 SHA256，同时验证根 LICENSE；缺失或内容改变均失败。准确产物、Full／Small、Store／Sideload 和双架构的实际结果以 Validation-0.0.25.2.md 为准。此许可核对不替代 WACK、Store 重签或实机认证。
