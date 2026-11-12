# 0.0.11.0 实现与验收记录

## 状态和边界

基于master／0d8b733（0.0.10.0）实施已确认的Ext前台监控方案。版本集中由Directory.Build.props升至0.0.11.0；保留HybridMount、HybridMount.Ext、HybridMount.Stock、HybridMount.Tests四工程、原输出及安装身份dwzcn.HybridMount.NativePreview，不恢复discard分支的.72改名、集中DLL或daemon。

开始菜单和安装显示名称统一为HybridMount.NET，三语言说明移除原生预览版字样；MSIX文件名同步。安装身份是兼容键，本轮不迁移配置或已有挂载。实际安装／升级后的系统显示缓存尚未验收，未安装生产包。

所有产品文本修改先写版本化补丁，git apply --check成功后git apply，成功删除工作补丁。升版前首批证据在artifacts/validation/00100-foreground/；后续补丁副本、构建、TRX、包哈希和探针记录在artifacts/validation/00110-foreground/，下文未特别注明的证据均相对此目录。忽略目录中的辅助脚本不作为产品组成。

本轮仅临时安装两个随机独立身份的菜单对照包，已精确卸载和清理；未操作正式挂载、未重启Explorer、未结束用户现有程序，不补跑既有.33停止专项。用户原有HybridMount.Ext/HybridMount.Ext.aps和HybridMount.Lib/保留。

## 已完成的实现

- 全局仅监听前台切换；其他明确对象／滚动事件限定当前Explorer的相关线程，回调先过滤窗口。删除所有窗口的周期扫描，无启用根时停止观察进程。
- 前台身份包含HWND、PID、线程和进程创建时间。Shell接口仅属目录STA；首次或导航失效时至多映射32个Shell窗口，缓存同框架所有候选，以可见ShellView消歧，有歧义不发布。导航连接点只置位代次和取消，不在通知栈做目录读取。
- 普通目录在根标记、UIA和名称解析前通过路径快照过滤；当前普通目录三十秒兜底，后台窗口不查询。前台挂载变化250ms合并、静止五秒补查，识别至采样共用250ms绝对期限，128点／64媒体项、单项在途。
- 前台／视图共享取消证据在UIA和名称解析前后核对，采样后再读URL，防止同一视图被导航复用。失焦、最小化或确认普通目录撤销浏览预取；同目标瞬时失败保留需求。超时5秒、连续失败30秒退避；不合作Shell／UIA由拥有者回收独立进程，至少30秒后重建。
- 内部心跳和变化快照分开，心跳只表示存活，不访问根或重复发布。相同需求去重，仅变化且非空的需求核验受影响根；撤销无需根查询。实际读取／图片发布的身份和内容版本保护继续执行，撤销浏览预取不影响用户下载或共享读取的其他消费者。
- thumbnailDiagnostics增加observer内存计数，涵盖范围、事件、映射、位置／根查询、采样、排队、活动和超时。读取诊断不启动观察、Shell、生成或源读取。新增隔离生产策略回归13项，测试角色仅存在于测试映像，生产命令行不提供该入口。
- 三语言PackageDisplayName统一HybridMount.NET，MSIX输出文件名同步；包检查从实际PRI验证三语言候选。公共IPC v2、配置、备份和SQLite格式保持兼容。

## 实际执行结果

本轮执行针对性回归，未重复上一版本的1759项全量结果。不同专项存在重叠，不累加测试数量。

| 项目 | 结果与范围 | 证据 |
|---|---|---|
| 最小x64 Debug原生构建 | 修正首轮缺少CloudFileOperations.h声明后通过 | 00100-foreground中的构建日志与补丁记录 |
| x64 Debug非打包及测试映像 | 通过，主程序、扩展、Stock及隔离映像均编译 | final-Debug-delivery.log（测试阶段曾失败，见下文）、build-tests-x64-Debug.log |
| 最终x64 Debug相关回归 | 121/121通过，0失败／0跳过 | final-Debug.trx、final-Debug-tests.log |
| x64 Release相关回归 | 121/121通过，0失败／0跳过 | targeted-Release.trx、release-x64-delivery.log |
| 首批策略／后续诊断专项 | Debug 55/55及53/53，与最终矩阵重叠 | targeted-Debug.trx、diagnostics-name-Debug.trx |
| x64／ARM64 Release及签名MSIX | 两种架构构建、签名及包检查通过；实际PRI三语言显示名均为HybridMount.NET | release-x64-delivery.log、release-ARM64-delivery.log |
| ARM64测试程序集 | 交叉编译0警告／0错误；没有执行ARM64测试 | tests-ARM64-compile.log |
| 资源及版本 | 1050键×3语言、8原生字符串×3；主程序／扩展／Stock文件版本0.0.11.0 | 两架构交付日志、final-products-and-cleanup.json |
| 菜单独立包 | 两种注册各七类传统菜单查询及保护／清理完成；自有菜单覆盖未证实 | menu-registration-comparison.json、menu-comparison-retry.log |
| 清理复核 | 两个探针包均不存在，两个自有测试根目录均不存在；用户现有程序保留 | final-products-and-cleanup.json |
| 最终静态检查 | 结果另存final-static-checks.json；只检查本版实际输入 | final-static-checks.json |

状态机／发布器回归覆盖：非Explorer不排队、前台合并、同框架导航与同名文件、失焦撤销与窗口复用、瞬时失败保留、5／30秒退避、心跳和相同需求不查根、普通目录三十秒兜底、后台／无关事件过滤、连续滚动、采样中变化、迟到结果、身份失败及路径边界。其余相关回归覆盖真实隔离原生菜单、缩略图队列、真实解码／正文预算、禁用及版本屏障、原生IPC和语言资源。这些隔离证据不等于真实Explorer界面验收。

## 独立菜单包对照及扩展评估

生产已使用desktop3:CloudFiles和ThumbnailProviderHandler，状态继续由StorageProviderItemProperties::SetAsync发布。CustomStateHandler、ExtendedPropertyHandler、BannersHandler保留现有GUID_NULL；ContentUriSource、DesktopIconOverlayHandlers和状态浮窗本轮不新增。清单注册本身不能缩小目录监控或消除同步等待。

使用同一菜单实现，以随机CLSID标签9ca27d87分别构建、签名、安装通配注册和CloudFiles专属注册的独立包，没有重复注册两个菜单。包名为dwzcn.HybridMount.NativePreview.Probe9ca27d87w及Probe9ca27d87c；两者均已卸载。测试根均在各自证据子目录，未接触正式根，没有远端文件源。

各自执行占位文件、占位目录、新建普通文件、混合选择、根目录选择、目录背景及外部普通文件七类真实IShellFolder／IContextMenu查询。前后持久身份、Pin、InSync、OnDisk和Modified状态保持，冷文件未水合，清理成功。但两种注册的自有HybridMount菜单计数均为0，系统保留／释放菜单仍存在；报告Passed仅代表该探针的保护、执行和清理断言，不能证明自有菜单覆盖完整。传统IContextMenu不代表Windows 11现代菜单UI，查询耗时也包含其他已安装扩展，不能作为本产品回调性能。生产菜单继续保留当前通配注册，现代菜单与CloudFiles专属注册仍待真实界面对照。

命名空间与最低系统边界：ContentUriSource和叠加图标属于desktop4；StorageProviderStatusUISourceFactory属于cloudFiles2，清单文档标22621，API文档标23504／CloudFilesContract v7，不能直接视作最低支持Windows 11 21H2可用。

参考资料（不是软件测试）：

- [desktop3:CloudFiles](https://learn.microsoft.com/zh-cn/uwp/schemas/appxpackage/uapmanifestschema/element-desktop3-cloudfiles)
- [desktop4:ContentUriSource](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-desktop4-contenturisource)
- [cloudFiles2:StorageProviderStatusUISourceFactory](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-cloudfiles2-storageproviderstatusuisourcefactory)
- [IStorageProviderStatusUISourceFactory](https://learn.microsoft.com/en-us/uwp/api/windows.storage.provider.istorageproviderstatusuisourcefactory)

## 空闲对照及性能边界

从已有0.0.10.0签名MSIX提取旧观察进程，与本轮观察进程分别运行约15秒；仅使用自有未注册隔离路径，不导航窗口、不访问源。执行上下文GetForegroundWindow为空，foregroundClasses记录为[""]，因此属于无前台目标的空闲场景。

| 计数 | 0.0.10.0 | 0.0.11.0 |
|---|---:|---:|
| 发现的窗口上限 | 2 | 0 |
| 累计目录样本 | 8 | 0 |
| 完整视图快照 | 61 | 1 |
| 纯心跳 | 0 | 60 |
| 新版映射／位置／根查询 | 旧版无对应计数 | 0／0／0 |

证据idle-observer-comparison.json及.log。该对照显示无前台目标时已避免后台目录工作；不代表真实前台Explorer、普通目录切换、Windows消息响应或全部性能目标通过。

仍待测：冷启动单列，暖态Shell回调P95≤50ms／P99≤150ms、Explorer消息响应P95≤200ms且无持续一秒无响应；普通目录切换P95相对基线增量≤max(50ms,基线10%)。需要实际窗口的未加载扩展／关闭缩略图／开启缩略图三态、千文件、多窗口、多标签页、快速切换与滚动和慢Shell／UIA注入，不以隔离调用替代。

## 阶段失败与复测

- 首轮原生编译缺少ValidateRuntimeRoot声明，加入现有CloudFileOperations.h后最小构建通过；升版前证据保留在00100-foreground。
- 独立菜单辅助脚本先遇到嵌套字符串解析错误，没有执行安装；随后测试包名超过50字符，MakeAppx拒绝，尚未安装。修正辅助脚本与短包名后两种独立包完成，失败日志保留在menu-comparison.log及相关子目录；没有扩大生产菜单注册范围来消除失败。
- Debug扩展矩阵曾为120通过／1失败／121总项，失败为PreviewPipelineTests.RealWorkerDecodesAllCacheSizesUsingBudgetedAnonymousPipe的JPEG分支，抛出10秒PreviewPhaseTimeoutException。原TRX保存在Debug-before-decoder-recheck.trx，日志final-Debug-delivery.log。未修改解码代码或放宽产品期限；单独原六种格式6/6通过（decoder-recheck-Debug.trx），随后同范围完整121/121通过（final-Debug.trx）。原因未确认，不能归因于并行菜单探针或声称修复JPEG超时。
- 收尾采集版本时首次使用了缺少TFM／RID的输出路径，Get-Item失败；改为实际输出路径重新采集，未重建或改动产品。

## 交付产物

使用既有当前用户发布者证书签名，未增加证书或修改信任；签名验证通过，未设置时间戳。包检查涵盖架构、0.0.11.0、自包含、MenusAndThumbnails、原生状态／维护入口及实际PRI显示名。

- x64：artifacts/packages/x64/HybridMount.NET_0.0.11.0_x64_Test/HybridMount.NET_0.0.11.0_x64.msix，SHA-256 `C7740D54198681A4CBAA700BDD2FB3568E48567ED7198626B7C6E5B9B9E9C866`。
- ARM64：artifacts/packages/ARM64/HybridMount.NET_0.0.11.0_ARM64_Test/HybridMount.NET_0.0.11.0_ARM64.msix，SHA-256 `B916DE50D61BA81FAE6BA552DA81E1D5F01FAADA3B59DF0FE9C5B56B1D8C6236`。

包哈希、文件版本、TRX计数及清理快照保存在build-delivery-summary.json。生产包未安装，系统开始菜单缓存是否已更新没有现场结论；用户需正常安装／升级该包后验收。Windows 11 21H2、真实DSM、ARM64实机、人工键盘及现代菜单交互均列待测。本轮仅本地提交，不据旧轮授权重复删除分支。
