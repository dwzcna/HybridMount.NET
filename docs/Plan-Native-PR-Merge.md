# codex/native-backend 本地 PR 整合计划（0.0.9.46—0.0.9.55）

编写日期：2026-11-09。本文为用户确认执行的分批计划，实际完成、测试和提交另行逐版本记录。

## 最新替换安排

用户最终调整为：.47修复后，先.48—.52整合PR，再.53备份、.54三线队列、.55工程整理；防止先重构扩大PR冲突。完整约定见Plan-0.0.9.47-0.0.9.55.md。下列安全修订继续适用，PR按现有目录整合，.55再统一迁移；历史调查不代表当前交付状态。

## 本地完成进度

.47已提交940c7d8；.48已完成#26/#32/修订#35代码和自动验证，来源及边界见Validation-0.0.9.48.md。 .49已完成#33/#24/#25代码及双配置59/59，详见Validation-0.0.9.49.md。 .50已修订#29并提取#36超时诊断，双配置34/34，详见Validation-0.0.9.50.md。 .51已成对修订#30/#37，Debug广泛1445/1445及Release相关183/183，详见Validation-0.0.9.51.md。 .52已整合#34/#31，双配置55/55，详见Validation-0.0.9.52.md。本地PR批次完成，后续保持.53备份、.54队列、.55工程整理。

## 已确认范围与前置条件

用户已授权在 0.0.9.45 完成提交后逐个整合 PR；相近功能或同一模块归入同一小版本，每批只形成一个提交，每次提交升版一次。保持本地交付，不 push，不操作 GitHub merge 按钮，不合并或改动 master。执行者在授权范围内按批推进，无需逐批重复询问授权。

本文编写时本地 HEAD 与 GitHub 目标分支均为 305b62e4e2985accbe650686544b0e129a2d2589；工作区正在实施 0.0.9.45，主代理报告最新针对性 42/42 通过，广泛回归仍在进行。该信息不是 .45 已提交或后续 PR 已通过验证的证明。第一批已在 .45 实际提交2b9d920后开始；.46代码与自动验证已完成，详见本版验收。不能覆盖、暂存或夹带当前 .45 工作区。

GitHub 调查共 17 个开放 PR，其中 #22—#37 共 16 个以 codex/native-backend 为 base；#21 以 master 为 base，排除本轮。16 个 PR 在旧基线上均显示 clean、各一个提交、No reviews、每 HEAD check-runs=0，仓库 Actions runs=0；作者均明确未编译或测试。clean 只说明各 PR 相对旧基线的文本状态，不能证明组合兼容、正确或已验收。未查询旧式 commit-status。

## PR整合与新增修复批次

| 版本／顺序 | 本批 PR 与内部顺序 | 责任模块 | 单个本地整合提交建议标题 |
| --- | --- | --- | --- |
| 0.0.9.46 | #23 → #22 → 修订 #27 → #28 | CFAPI 事件、元数据切换、fetch 与连接生命周期 | 0.0.9.46｜保全原生事件与读取断开生命周期 |
| 0.0.9.47 | 新增：Explorer缩略图浏览回归修复 | 观察、需求、取消与缩略图读取 | 0.0.9.47｜修复缩略图浏览与异常重试 |
| 0.0.9.48 | #26 → #32 → #35 | RuntimeHost 调度、握手和失败诊断 | 0.0.9.48｜隔离原生长操作并补齐握手诊断 |
| 0.0.9.49 | #33 → #24 → #25 | 启动恢复、双部署配置与 UI 原生调用 | 0.0.9.49｜异步核对挂载并修复跨模式恢复 |
| 0.0.9.50 | 修订 #29；复核 #36 后提取必要增量或记录不另合冗余 | 本地预览租约与缩略图读取 | 0.0.9.50｜回收预览读租约并保全缩略图校验 |
| 0.0.9.51 | 同批修订 #30 → #37 | 原生／托管检查点事务、取消与停止 | 0.0.9.51｜统一检查点取消与不可逆阶段收尾 |
| 0.0.9.52 | #34 → #31 | 操作说明与故障后手动批量重连 | 0.0.9.52｜说明拒绝移动并支持故障后批量重连 |
| 0.0.9.53 | 新增：配置备份维护 | .hymcfg、可选密码、原格式兼容 | 0.0.9.53｜调整备份后缀并支持可选密码 |
| 0.0.9.54 | 新增：统一任务队列 | 挂载网络步骤、优先级、安全让路、任务页 | 0.0.9.54｜统一挂载任务调度与等待状态 |
| 0.0.9.55 | 新增：工程与目录整理 | 主工程HybridMount.NET、原生HybridMount.Stock及MVVM职责 | 0.0.9.55｜规整工程命名与代码职责目录 |

这六批覆盖 16 个 PR。#36 不单独占版本；如果确无必要代码增量，其处置说明并入 .50 的同一提交。#30/#37 是同一事务链，不能为凑版本拆开。每批结束后才进入下一批。版本号是第四段递增，集中修改 Directory.Build.props；主程序、扩展、原生后台、测试、程序集及 MSIX 使用同一四段版本，不再追加 .0。

## 2026-11-09 优先级调整

.45已提交2b9d920；.46整合#23/#22/修订#27/#28，Debug限制并行广泛1363/1363。用户报告仅开启显示缩略图后Explorer强制下滑，并伴随取消及IO异常、坚果云相册不出图，因此插入.47专项；原.47及以后整体顺延一版，相近PR仍按原组合顺序。不将未知根因当作已修复，不用强制滚回掩盖；只使用隔离测试根，真实相册先只读诊断，人工复测列待测。

## 本地整合和提交方式

1. .45 提交完成后，检查工作树和既有暂存。固定本批 PR 的完整 HEAD SHA；后续获取发现 SHA 变化时，重新只读检查新差异，不默默替换调查版本。
2. 只使用 GitHub 只读 API 或 git fetch 获取 PR HEAD 对象。获取是本地读取，不执行远端合并、关闭 PR、推送或改写 master。PR 在 GitHub 上仍可能显示 Open，完成报告应说明已本地整合和来源 SHA。
3. 按 AGENTS 的补丁流程，将本批全部修改、必要修订、版本及记录先写入 patch/0.0.9.xx_功能名称.patch，执行 git apply --check，再 git apply；成功后删除补丁。不能先 git merge／cherry-pick 修改产品文件，再补写补丁。
4. 采用每批一个本地整合提交（相当于本地 squash）：本批各 PR 逐项审阅和整合，最终共同提交一次；不为每个 PR 创建中间提交，不保留未升版的 WIP／冲突修复提交。来源编号和完整 SHA 写入本批文档及提交正文，便于追溯。用户要求允许一个整合提交，不要求把全部 PR HEAD 保留为提交父节点。
5. 运行本批验证，失败在同一批中继续以补丁修订；验证后补丁更新真实记录，再仅暂存本批文件。检查全部暂存内容，避免夹入其他任务内容，提交后核对版本及提交差异。本批提交包含且只包含一次版本递增。
6. 日志放 artifacts/validation/009xx-pr-merge/。README 仅更新实际已交付版本／入口；Changlog、HybridMount-devplan 和 docs/Validation-0.0.9.xx.md 分别记录代码状态、实际测试及人工待测。不能把计划、编译尝试、历史 .45 结果记为后续版本通过。

## 0.0.9.46：CFAPI 事件和连接生命周期

来源：

- [#23](https://github.com/dwzcna/HybridMount.NET/pull/23)，2c26e47d9579b32cb9730546ce8cead6e855e617：把 blockEntry IPC 移出 _fetchGate。
- [#22](https://github.com/dwzcna/HybridMount.NET/pull/22)，aeeceef817e3e2c37fb73d108efcc122a8e4b733：状态事件背压及发送失败显式故障。
- [#27](https://github.com/dwzcna/HybridMount.NET/pull/27)，5651294371197ce21e969988b32daad29f4622a6：finishFetch 重试及陈旧 fetch 回收。
- [#28](https://github.com/dwzcna/HybridMount.NET/pull/28)，620e495c1ed6f3d8985bcbad72a2f51270ea450f：断开失败保留连接句柄，回调上下文安全兜底。

文件所有权归本批同一执行者：HybridMount.Native/CloudSession.cpp、CloudSession.h、RuntimeHost.cpp 的 Close 部分、RuntimeHost.h；HybridMount/Services/Mounting/CFAPI/CloudMountSession.cs、CloudMountSession.RemoteContent.cs；NativeCloudSessionTests、ReadOptimizationTests 及必要隔离故障注入测试。共享版本和文档也由该执行者串行维护。

必要修订与整合：

- 先解决 #23 的事件循环锁等待，再启用 #22 背压。保留 .45 previewDemand 分支和专用预览消费通道，不能把可丢弃预览提示提升为关键事件。
- #22/#27/#28 共同修改 CloudSession.cpp；合并 Event 的等待参数、Fetch 活动状态、CallbackGate／Detached 和连接重试，不在冲突处整段选任一侧。
- #27 的 120 秒 LastActivity 只观察 TRANSFER_DATA，可能取消尚在等慢服务器的合法读取。必须给“对端完成确认丢失”和“活跃但暂未产出正文”建立可验证区别；无法证明安全的定时回收部分先改为明确故障／连接代次收尾，不能直接按静默时间解除有效读取保护。可保留有界幂等完成重试，但不能声称未实现的泄漏恢复已完成。
- #28 在 CfDisconnectSyncRoot 成功后才清空 Connection；失败保持可重试。析构兜底只能在回调排空且 Detached 后保留必要上下文，不能继续访问已销毁 Host。

精准验收：x64 Debug 最小编译；ReadOptimizationTests 完成重试／终态错误；NativeCloudSessionTests 真正 fetch／重复 finish／断开；补充满队列、元数据切换迟到 fetch、慢首字节超过原回收阈值、丢完成回复、断开失败后重试、断开失败且回调在途的测试。需有测试专用故障钩子时只在 HYBRIDMOUNT_NATIVE_TESTS 生效。确认无死锁、无丢关键事件、无提前解除忙碌保护、无回调 UAF。该批生命周期变动范围较大，针对性通过后做一轮 x64 Debug 广泛回归，排除 .33 停止专项。

## 0.0.9.48：调度与诊断

来源：

- [#26](https://github.com/dwzcna/HybridMount.NET/pull/26)，b0a3f6d39f1f7ece887930d81002c69bd0c00ff0：水合／远端快照采用独立长任务池。
- [#32](https://github.com/dwzcna/HybridMount.NET/pull/32)，8531f26c136849937bee847b207e231098b84f56：缺失 EXE、版本、父进程及提前退出诊断。
- [#35](https://github.com/dwzcna/HybridMount.NET/pull/35)，28b8a14d065fab86f86a2bdadeebc9b983e346c4：JSON／内存等异常分阶段返回。

文件所有权：HybridMount.Native/RuntimeHost.cpp、NativeDiagnostics.h；NativeRuntimeClient.cs、LoggerService.Hosts.cs；NativeRuntimeTransportTests、LogHistoryTests。不得回写 .46 的 CloudSession 业务语义。

整合要点：同一个 worker lambda 必须同时保留 #26 的长任务路由与 #35 的异常映射，Close 保留 .46 的 OutGate 同步。Run 和析构都停止新增池。握手重构成功后保留 .45 普通事件、预览事件、控制和正文读取任务启动；父映像／SID／会话／包／nonce 校验不能放松。只导入白名单阶段和数字版本，不把 JSON 异常正文中的业务参数写入日志。

精准验收：NativeRuntimeTransportTests、LogHistoryTests 中新增方法；4 个长操作占满时 queryFile/readLocalRange/connectRoot 等短操作仍可完成，取消与停止释放所有池；缺失 EXE、匹配／错版本／缺 build、错 nonce、原生提前退出；畸形 JSON 返回安全阶段且运行时按协议策略存活或关闭；无参数／凭据泄漏。最小 x64 Debug 和相关 Release 编译及测试通过，原生 ARM64 交叉编译确认异常和线程池代码可编译。

## 0.0.9.49：启动、配置和跨模式恢复

来源：

- [#33](https://github.com/dwzcna/HybridMount.NET/pull/33)，39727a4cb885b20761689da45af20e12b1df4de1：正确读取 Packaged／Unpackaged 的挂载定义键。
- [#24](https://github.com/dwzcna/HybridMount.NET/pull/24)，d3a7e54a19121464c4cbdceaf65a1081f98e22b1：启动核对移出 UI，共享每根核对任务。
- [#25](https://github.com/dwzcna/HybridMount.NET/pull/25)，d53109f6f594fd937750508e6034e5adb1a31463：新挂载／配置／回收操作离开 UI。

文件所有权：ApplicationPaths.cs、RegistryMountDefinitionStore.cs、MountManager.Recovery.cs、MountManager.cs 的构造／初始化／配置入口、MountPresentation.cs、MountsViewModel.cs、SettingsViewModel.cs、两个配置对话框；ContractTests、PageViewModelTests，以及 FailedMountTests 中本 PR 相关的精确方法。

整合要点：#24/#25 的 UpdateCredentialsCoreAsync 同时保留 ForceYielding 和 EnsureInspectionAsync；所有连接／编辑／断开／移除入口必须等待同一核对任务后再取得根锁。保留 .45 QueueLocalPreview 和预览观察初始化。双模式路径保持现有存储布局，只修正读取位置，不迁移或创建另一模式的配置，不将其受管根冒认为遗留根。

精准验收：ContractTests 的双模式路径／不存在键不创建；PageViewModelTests 的检查中禁用、完成刷新、失败保留恢复；UI 不泵消息时构造、启动、创建与保存可完成；检查中用户取消／退出不挂起；配置对话框关闭后迟到结果不更新控件；回收统计也在后台。#25 的 AsyncRemoteRootCheckRejectsMissingMountAndCancellation 单独筛选运行，必要既有配置方法按精确名称列入；禁止以整个 FailedMountTests 类代替精准筛选，禁止启动 OrphanedMountRecoveryTests、ClearRemovalTests 及 .33 已停止专项。真实 WinUI 启动、配置和回收交互若无人操作则保留待测。

## 0.0.9.50：预览租约与 #36 处置

来源：

- [#29](https://github.com/dwzcna/HybridMount.NET/pull/29)，247df8897371c9321e03c61a8692e29c0446553d：未确认本地读租约回收。
- [#36](https://github.com/dwzcna/HybridMount.NET/pull/36)，08d5652068f6ea84855c72578d019f289b5ebbfc：缓存读期限 500 ms／诊断，须重新比较最终 .45 差异。

文件所有权：LocalReadLeases.cpp、NativeLocalReadLease.cs；确有必要才修改 ThumbnailCache.cpp；NativeCloudSessionTests、NativeLocalThumbnailTests、BrowseThumbnailTests、必要预览缓存测试。LocalThumbnail／PreviewObserver 的现有算法由 .45 保持，未发现必要性不扩大修改。

#29 修订：在 .45 的 Verify、512 次／128 MiB 预算、Cloud 状态校验和 closeLocalRead 最终 Stamp 返回之上增加租约活动／回收。不能直接恢复旧 32 次／8 MiB 或严格旧 Key 比较。惰性回收仅随下一条命令触发，无法保证无人再请求时归还 oplock，应提供生命周期内可停止的可靠回收或明确所有权确认机制。不能在全局租约锁下无期限等待活跃读的锁；有效解码／读取不能被当作泄漏。C# 不能简单吞掉全部关闭异常，导致最终 Stamp 和真实校验失败丢失；允许的传输关闭兜底与业务校验错误分开处理，并保留诊断。

#36 的可核查依据：当前 .45 已从 ReadCachedThumbnail 改为 ReadAvailableThumbnail，新增本地完整正文解码／授权，SqliteCheck 保留 BUSY／CORRUPT 等真实错误，失败返回实际 HRESULT，并按 ShellServer／Thumbnail.<stage> 进行 60 秒限流。#36 原稿仍只调缓存读取、统一返回 NOT_FOUND，日志每秒可写一条，不能原样合并。当前 .45 数据库期限仍是 100 ms，所以也不能宣布 #36 全部已覆盖。

执行 .50 时用最终已提交 .45 和实际当前代码作增量清单：保留仍必要的超时识别和分阶段证据；500 ms 必须满足缓存路径及本地解码整体预算并由冷缓存／延迟场景证明，不把“SQLite 打开后 500 ms”误写成整个 Shell 调用的硬期限。若当前 100 ms 已满足验收、诊断已覆盖且放宽无收益，文档明确记录 #36 不另合冗余／不采用期限放宽及证据；若需要则只在本批提取最小增量，不多建一次提交，不自行远端关闭 PR。

精准验收：丢 open 回复、调用者取消、close 失败、超过回收阈值且没有后续请求，确认句柄／名额最终释放；活跃慢读和 .45 最终 Stamp 正常；本地完整／仅联机、改名／内容变化／关闭开关／移除，确保不越界、不错误水合、无陈旧图。核验 SQLite 缺失／繁忙／损坏／真实超时阶段及日志限流。最小 x64 Debug、针对性 Debug／Release；涉及 Shell 读取入口变动则重做独立 COM／真实隔离包路径核验，Explorer 目视另列实际结果。

## 0.0.9.51：检查点成对修订

来源：

- [#30](https://github.com/dwzcna/HybridMount.NET/pull/30)，c62582e0ea490b1f76dfbcb19cc6f98ec75ea261。
- [#37](https://github.com/dwzcna/HybridMount.NET/pull/37)，736b553c63fe98d619c907161f66f63e1b324b76。

文件所有权：RuntimeHost.cpp 检查点部分、RuntimeOperations.cpp 测试命令、NativeRuntimeClient.cs、NativeRuntimeProtocol.cs；CloudConflictReplacement、CloudRemoteUpdate、CloudUploadIssueProperty、CloudMountSession.Pinning；MountManager 的冲突／PinIntents／RemoteApply／UploadIssues 及对应构造委托、FileExclusionMarkerStore；两 PR 全部受影响测试调用点。本批负责全链条签名迁移，不能只改接口后把调用点修复另放一版。

必须修订：#30 的无条件 command.Canceled() 会使 safety 检查点等待提前失败；#37 将已取消 caller token 传入 safety handler 并立即 ThrowIfCancellationRequested，可能拒绝已经开始的不可逆写入证明／落库。先定义并实现“新工作可取消、不可逆阶段必须安全收尾、宿主故障保留未知结果／恢复证据”的统一规则。writing、commit、remoteHydrating、remoteCompleted、propertyWritten／Commit／Proof、exclusionCommit／Proof、pinReleasing 均需逐阶段核对。不能以调用者取消推断写入回滚。

保留 .45 的双事件 worker 关闭和 .48 的诊断／握手逻辑；保留 .45 QueueLocalPreview，并把 PinResourcesReleasingForTests 委托改为 token 形式。#30 testCheckpoint 与 #37 testManagedCheckpoint 可保留两个独立测试操作；若统一名称则同批迁移全部测试。#30 的两参数 handler 同批转为三参数。

两分钟 CTS 是合作取消请求，不是处理器硬期限。不得在处理器尚未结束时提前 ACK 成功，也不能停止等待后让迟到处理器继续写已经释放的 repository。停止超时需要明确迟到写入屏障／所有者寿命及“结果待核对”证据；保留恢复日志和原快照。

精准验收：NativeRuntimeTransportTests 的 >30 秒 ACK、取消到达、ACK 不早于 handler 返回、进程死亡、忽略取消的 handler 与停止；新增在每个不可逆检查点前／后取消的真实隔离根测试，确认正文、InSync、恢复阶段、SQLite 与本地备份一致。覆盖 NativePinIntentTests、RemotePinnedNativeTests、ShellIssueMissingFileTests、ShellItemPropertyTests 受影响方法以及冲突替换／远端应用的既有相关测试。停止后释放检查点钩子，证明不会对已销毁仓库迟到写入。最小 x64 Debug 后，针对性 Debug／Release及广泛 x64 Debug 回归通过再提交。

## 0.0.9.52：用户恢复和拒绝移动说明

来源：

- [#34](https://github.com/dwzcna/HybridMount.NET/pull/34)，2cdbf65f7333f608c44bb2bf8888e338e69c4f24。
- [#31](https://github.com/dwzcna/HybridMount.NET/pull/31)，d84d3b3df351b7fb61fb9c0d7b8930b1da06c8c6。

文件所有权：CloudSession.cpp 的拒绝改名通知；CloudMountSession.cs；MountManager.cs、MountManager.RuntimeRecovery.cs、MountManager.UploadIssues.cs；UploadIssueNotificationService、MainWindow、MountsViewModel、MountsPage.xaml；UploadIssueIntegrationTests、PageViewModelTests。UI 文件登记及 Compile／Page 实际项必须核对。

整合要点：保留 .46 回调保护／背压，renameRejected 作为非关键说明事件丢失不能改变拒绝结果；C# 同时保留 .45 previewDemand 与本事件分支。MountManager 的连接／断开入口保留 .49 的 EnsureInspectionAsync；.51 token 委托保留。批量重连必须等待原故障收尾，串行逐根、重新核验 RuntimeFailed，用户主动断开／移除及已连接根不被重连；不自动重放未知写操作，不根据 AutoConnect 排除曾手动连接的根。

精准验收：拒绝移动的原因、代次过滤、去重／关闭、销毁后迟到通知；真实隔离根移出／跨根拒绝。批量重连无故障空操作、手动根、单根失败继续、重入、收尾中用户断开、移除／正在连接根跳过。除反射单元测试外，杀死隔离原生进程，证明根真正进入故障且能由按钮流程重连；不杀正式后台。WinUI InfoBar 与重连按钮目视列实际完成／待测。

本批最终针对性通过后执行本系列最终 x64 Debug／Release 广泛回归（保持 .33 停止专项排除）、必要 WinUI／非打包构建、x64／ARM64 签名 MSIX 和 Test-Package 内容核验、ARM64 测试交叉编译及生产边界审计。真实安装升级、Explorer 和 ARM64 实机只有实际执行才能写通过；无法执行时明确待测及影响，不以交叉编译代替实机。最终结果写 .52 文档，同时保留各批真实记录。

## 构建、验证和回退约束

仓库入口是 build/Build.ps1，参数包括 -Platform x64／ARM64、-Configuration Debug／Release、-Deployment Packaged／Unpackaged、-Test、-Package。使用 global.json 固定 SDK 和已有 VS／Windows SDK。先按脚本实际调用构建 Ext 和 Native 及隔离测试组件，再 dotnet test 指定精准 --filter、--results-directory；不要凭旧项目命令或直接运行陈旧原生测试二进制。相同输出目录串行复用缓存。build/Build.ps1 -Test 默认广泛筛选排除了 FailedMountTests、OrphanedMountRecoveryTests、ClearRemovalTests，不能删除排除项来方便跑新测试；新方法按名称另跑。

各批提交前保存本批完整日志、源码／版本和测试二进制对应证据。固定版本的一次通过检查输入未变不重复；后续改动涉及相同链路时补必要复测。命令三分钟无新证据时检查进程／日志，不重复启动有效任务。

已确认的本地文本冲突快照：#29 LocalReadLeases.cpp 3处、NativeLocalReadLease.cs 1处；#32 NativeRuntimeClient.cs 1处；#34 CloudMountSession.cs 1处；#36 ThumbnailCache.cpp 5处；#37 MountManager.cs 和 NativeRuntimeClient.cs 各1处。PR 间还有 #22/#26、#22/#27/#28、#24/#25/#31、#26/#35、#30/#37 等冲突。证据见 conflict-simulation.jsonl 和 snapshots/；.45 仍在修改，实际整合以其最终提交重算，不依赖旧冲突块数量。

失败留在当前批修复，不带病开下一批。未提交的本批失败只通过明确反向补丁回退本批相关修改，保全其他任务内容，禁止 git reset --hard／git clean。已交付批次原则上向前修复；若需回退代码，则新建下一版本的单个回退整合提交，保留受影响来源、证据和数据兼容说明，避免恢复旧 Version 造成版本倒退。原生不可逆操作的回退不能以代码撤销代替恢复日志处理。

## 工期与执行人

默认同一执行者拥有六批，串行修改共享文件；本计划子代理只负责只读调查与计划正文，不承担产品代码修改，执行期间不另启代理。本轮计划无需构建，也没有运行产品测试。

估计代码整合／必要修订／自动验证及记录合计约 24—40 小时：.46 5—8小时，.48 3—5小时，.49 3—5小时，.50 4—7小时，.51 6—10小时，.52 3—5小时；批次之和为 24—40小时。真实 WinUI／Explorer、安装及 ARM64 人工等待另计。#27 安全回收、#29 可靠租约回收和 #30/#37 取消收尾是主要不确定性；出现超预算先报告具体原因和剩余估计，不省略必要验收。

## 调查材料与限制

本目录 list.json、pr-N.json、files-N.json、checks-N.json、page-N.html、actions.json 与 conflict-simulation.jsonl 是本次调查快照；非产品文件、保持忽略，不纳入提交。GitHub 插件工具不存在，gh CLI 未安装，采用公开 REST／网页及本地 Git 只读比较。曾遇默认沙箱 helper 启动故障，按主代理确认改用 exec_command 的 require_escalated 运行只读命令；没有 UAC 提权或索引／refs 修改。
