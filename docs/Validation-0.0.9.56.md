# 0.0.9.56 主工程／输出改名与GitHub收尾验收

2026-11-10，在codex/native-backend的0753a66（.55）上实施。工作区开始时干净；四工程、Debug／Release、x64／ARM64、HybridMount.Stock及.55职责布局保留。证据目录artifacts/validation/00956-main-name/，本版代码只本地提交，不推送、不改变master。

## 实际实现

主目录／项目恢复HybridMount/HybridMount.csproj，AssemblyName与输出为HybridMount、HybridMount.exe／HybridMount.dll。同步解决方案、Tests源码链接、构建／图标脚本、MSIX主入口／维护／登录启动、原生父映像校验、Shell菜单启动及缩略图能力判断。包检查拒绝夹带旧HybridMount.NET主映像、程序集和运行配置。

NativePreview包身份、AUMID、COM CLSID、根ID、管道、注册表／数据路径保持兼容。ApplicationPaths、IpcMessages、MountBackupCodec、MountDatabase、RegistryMountDefinitionStore与.55文本相同（只迁路径）。Compile260／Page12／ApplicationDefinition1，零遗漏或重复，见source-and-compatibility.json。

已有自有根图标只尝试更新IconResource，先核对根／描述符ID和路径，保留返回的原描述符；按ID查询不可见时尝试目录查询，仍失败则记安全HRESULT并保留注册。不据此重建策略、转换文件、清空或接管根。此项代码已实现，真实旧图标刷新尚未通过验证，见下文。

## 实际验证及边界

- 最小x64 Debug非打包构建通过，输出新主映像；最终矩阵重建Debug／Release非打包、x64／ARM64 Release签名MSIX及包内容／PRI检查，另编译ARM64 Release测试工程。最终日志和产物摘要见final-matrix-driver.log及final-artifacts.json；交叉构建不代表ARM64实机。
- 最后针对性Debug为130/131，0跳过；NativeLocalThumbnailTests.LocalBudgetAcceptsMediaLargerThanRemoteBudget在十秒解码期限内未结束，单项隔离复测0/1同样超时。相关解码／预算源码本轮未改，原因未证实，不称随机失败、已修复或通过。原始final-targeted-Debug-attempt1.trx／log及large-media-isolated.trx／log保留。
- 最后Release针对性131/131、0跳过：契约、原生运行时／传输／检查点、缓存与完整本地缩略图、Shell能力、扩展、模块边界、页面／窗口ViewModel与外观等。见targeted-Release.trx／log。前期结果不替代最后代码验证，也未补跑.33已停止专项或重跑无关1495项全量。
- 最终独立真实包desktop3NameFinal-280f9507使用HybridMount.exe、生产HybridMount.Stock.exe及独立CLSID。包外Shell自动取得冷缓存图与完整本地图片，分别64×32、2048个预期像素；菜单含原生固定／释放，状态列System.StorageProviderUIStatus保留；实际AUMID为本轮Probe280f9507包!App，不能冒充正式安装身份。NativeTrace确认真实原生入口，源请求8→8，稳定文件身份／长度／写入时间／Pin／InSync及磁盘／修改范围保持。Usn／ChangeTime的观测差异保留，未要求其恒定。见binding-summary.json与原始result.json。
- 同包新建独立空根，卸载后包外验证NativeRootAbsent／RegistrationAbsent／NamespaceAbsent均为true。前两轮失败包也单独卸载并核验，未重启Explorer或操作生产相册。

## 最终签名包

双架构均经签名验证、包入口／架构／原生无CLR头及13项XAML资源检查。实际包：

| 架构 | 产物 | SHA256 |
| --- | --- | --- |
| x64 | artifacts/packages/x64/HybridMount_0.0.9.56_x64_Test/HybridMount_0.0.9.56_x64.msix | 5a2296f64b736b57ef3814e884b1fe447a95645c75d54de6ec78725ded10462f |
| ARM64 | artifacts/packages/ARM64/HybridMount_0.0.9.56_arm64_Test/HybridMount_0.0.9.56_arm64.msix | 0a91472175f9c7bfe78b4ce9ca8e6001da81be3aa35d1963f590165976438711 |

## 保留的失败与未完成验证

旧根图标专项共三轮：先GetCurrentSyncRoots无匹配，改为准确ID查询仍0x80070490，目录查询也0x80070490。三轮均在读取描述符时失败，尚未执行旧IconResource写入及后续保留全部描述符字段的断言；不记作刷新成功。显式探针NativeRootIconProbe保留以便后续真实身份复核，完整icon-proof.json及Cleanup=true均保留；自动缩略图／卸载检查单独完成，不能替代该项。

最早普通xUnit旧根图标用例131通过／1失败保留initial-targeted-Debug证据，后来移为显式包探针；曾出现辅助PowerShell只读变量名及缺NativeDiagnostics包含导致工具／编译失败，均已修正。最初一轮签名包早于最后原生查询修改，不当作最终产物。中间目录MSB8028保留，是.55前Native工程旧缓存提示，未为消除警告清空增量缓存。

Debug大媒体超时、旧根图标实际刷新、人工WinUI／Explorer目视、真实相册／鼠标、正式安装升级及ARM64实机待测。没有将无法执行的人工项目记为通过。

## 精确清理与GitHub结果

本轮普通测试注册备份128项、实际删除128项；真实包登记备份22份、实际删除16项（注册视图存在共享，删除后其余记录已不存在），核验目标剩余均0。180个符合当前SID、唯一GUID根及失效路径证据的测试缩略图库经哈希备份后删除，三个非目标库保留。三临时包、7份根证明经包外审计，无对应COM／同步根／Namespace／隐藏值／缓存残留；尚存卸载空目录的CFAPI查询及空目录核验通过。见00956-test-*、00956-packaged-*及packaged-final-audit.json。不处理用户早已清理的三个Thumbnail-00945入口。

按明确授权完成#22—#37共16个GitHub PR的Closed及来源删除；其中#23先前已完成，其余15项本次完成。merged_at均为空，不声称GitHub Merged。删除前保存本地refs/archive/native-pr-<编号>-00956并验证固定HEAD、整合提交及依赖，删除后逐项新鲜查询。仅#21仍Open，远端仅master、codex/native-backend和#21来源；两个远端目标与本地master未变。详见[PR精确收尾记录](GitHub-PR-Cleanup-0.0.9.56.md)。
