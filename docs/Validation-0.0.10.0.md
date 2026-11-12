# 0.0.10.0 源码与验收记录

## 状态和边界

基于master／630a499（0.0.9.71）实施，Directory.Build.props统一版本0.0.10.0。保留HybridMount、HybridMount.Ext、HybridMount.Stock、HybridMount.Tests四工程及HybridMount.exe／NativePreview身份，不恢复discard分支的.72改名、集中DLL或daemon。

首批仅落盘源码和静态检查；用户随后明确授权继续构建、验证、提交并推送GitHub及清理旧分支。本记录列最终实际执行结果。原有HybridMount.Ext/HybridMount.Ext.aps、HybridMount.Lib/保留；未安装新包、未操作正式挂载、未重启Explorer，未改写现场恢复记录。旧版通过结果不作为本版证据。

所有目标文本先写patch/0.0.10.0_*.patch，git apply --check成功后git apply，成功删除工作补丁。副本、应用日志和构建回归证据位于artifacts/validation/00100-work/。辅助脚本保存在忽略目录，不进入产品。

## 已完成的实现

### 云调度和设置

- 设置“云操作线程数”保存当前运行模式的 CloudOperationConcurrency；整数1—128、默认4，建议值 max(1,min(128,Environment.ProcessorCount/2))，不自动覆盖。明确使用异步任务和线程池，并非创建N条系统线程。读取缺值不写入，类型／范围或访问异常保留诊断；非法草稿及保存失败不改变生效值。保存成功才动态应用；减少额度不取消已有正文。
- 每挂载上传／修改、普通下载、独立元数据、缩略图远端读取各一路。完整文件租约不占全局额度；HTTP实际请求至正文EOF／关闭／错误才归还额度，依赖、退避和解码不占额度。修改内验证继承业务通道，群晖POST不按动词误分。相同内容版本下载消费者借用同一文件流程，共享工作接纳前同步保留所有者，等待者不重复申请源读取额度；后到高优先级仍提升实际所有者。
- 通道／路径／依赖就绪后授予文件流程，原优先级和同挂载提交顺序保留，同优先级跨挂载轮转。文件工作流、普通请求及已有流程子请求使用独立有界接纳，避免未来任务把当前所有者的网络入口堵满。
- 上传、下载、远端应用和改名协调同一路径／条目，目录删除和移动包含子树及目标路径；只读元数据可完成共享证明。父目录创建及子项删除依赖从原持久记录重建并重验，失败、取消、结果未知不视作成功；已发上传可继续只读核对／本地收尾，不重放写入。扫描与应用互斥分开，保留原根锁外网络和提交短锁保护。
- 连接退役停止接纳、取消各通道并等待文件／共享／正文子引用；响应返回及正文交付重验代次，迟到旧结果不能发布。配置、公共IPC v2、备份v5／v6和SQLite格式不变。

### UI和原生分层

- 扫描与应用分锁后的事件写入在同一SQLite事务内核对终态和身份；本地改动前固定已有Committing阶段，原生提交重验内容版本，旧准备不得复活Superseded／Completed。群晖列表版本补全仍允许，迟到暂存不会应用；不新增表、列或格式。
- 应用拥有的操作先异步让出UI调用栈，数据库初始化、任务批量快照／速度整理／排序在后台；250ms合并，仅通过Dispatcher更新绑定。任务结果、定位、重试、取消及副本提交由应用后台所有者执行，页面退出不取消业务；更换查询、关闭对话框、释放页面均挡住迟到展示。沿用.71挂载页不可变内存快照及日志异步查询。
- 菜单独立STA，缩略图独立MTA，工厂注册、撤销和活动引用排空由协调入口统一管理。快速GetState仅比较已有接口地址和250ms内存摘要，不调用Shell项目、PIDL或封送；无新鲜摘要返回E_PENDING。允许慢调用后捕获并交双线程／16项队列，Invoke完整核验。缩略图对象的路径状态加锁，锁外查询及解码。
- 观察器控制／心跳不执行Shell或UIA；目录识别STA拥有Shell接口，UIA客户端仅属MTA。缓存窗口映射、按窗口合并事件，普通文件系统目录在根查询／UIA前过滤。250ms绝对期限包含目录识别和采样，128点／64媒体上限保留；每次一项在途、迟到丢弃，5秒补查，5／30秒退避。持续不合作的Shell／UIA由父进程清理自建Job，至少30秒才重建；无效样本保留需求，多窗口继续合并。
- 普通8、属性4、长任务4、完成／释放2路，各池512项，规模不随云设置改变。finishFetch、closeLocalRead、blockEntry、clearRoot不被属性查询占满；控制读取不等待满响应队列，控制／数据／取消及检查点处理继续独立。固定池排队、活动、拒绝、运行／等待耗时，观察阶段及根／SQLite／身份／图像查询均有只读计数。
- 沿用托管一路、原生一路、同文件命名互斥、取得互斥后再取保护租约、一次解码派生四尺寸、原有Job／低优先级／预算／期限。C#唯一持久缓存写入者、250ms合并及每秒最多8项单文件通知保持。查询诊断不生成、不取源。

## 最终实际执行结果

证据根为artifacts/validation/00100-work/。构建命令使用build/Build.ps1；测试采用隔离原生映像及回环夹具，不连接正式挂载或外部云服务。

| 项目 | 实际结果 | 证据 |
|---|---|---|
| x64 Debug非打包及隔离映像 | 通过，资源1050键×3／8原生字符串×3 | final-managed-x64-Debug-Unpackaged.log、final-native-tests-x64-Debug.log、final-runtime-tests-x64-Debug.log |
| Debug全量回归 | 1759/1759通过，0失败／0跳过；显式排除FailedMountTests、OrphanedMountRecoveryTests、ClearRemovalTests停止专项 | final-Debug.trx、final-tests-x64-Debug.log |
| x64 Release非打包与相关回归 | 构建通过，492/492通过，0失败／0跳过；覆盖本版新增、修改及首轮失败类别 | final-Release.trx、final-managed-x64-Release-Unpackaged.log、final-tests-x64-Release.log |
| 探针／夹具最终专项 | Debug 9/9、Release 9/9通过；计数与完整回归有重叠，不累加 | fixture-probe-Debug.trx、fixture-probe-Release.trx |
| 共享资源夹具最终专项 | Release 23/23通过；报告取消、属性退避、缓存清理及异步取图 | stabilized-fixtures-Release.trx |
| 本地提交访问拒绝及容量恢复 | Release 40/40通过；验证预算有界、回执保留、正文仅上传一次 | local-commit-retry-Release.trx |
| ARM64 Debug非打包／Release打包 | 交叉构建通过，不在x64主机执行ARM64程序 | final-managed-ARM64-Debug-Unpackaged.log、final-managed-ARM64-Release-Packaged.log |
| x64／ARM64签名MSIX及包检查 | 通过，版本0.0.10.0／自包含／MenusAndThumbnails；签名证书与发布者匹配，未安装 | final-products.log、final-signature-x64.log、final-signature-ARM64.log、build-delivery-summary.json |
| ARM64测试交叉编译 | 0警告／0错误；未运行ARM64测试 | tests-ARM64-Release-compile.log |
| 静态与资源 | C#普通／headless语法0错误；317项资源、结构、词法检查0错误，1050键×3 | static-syntax.txt、static-structure.txt、source-sha256.json |

隔离回归覆盖云额度1／4／128、四通道并行／通道串行、动态升降、工作流不占云位、真实正文所有权、共享取消／生产者保留、路径及子树、父子持久依赖、重连退役、后台快照与迟到展示；WebDAV／S3／群晖夹具、真实隔离CFAPI根、原生菜单快速调用与延迟池、异步本地补图、冷文件提示、禁用和内容变化屏障、缓存／身份／Pin／InSync／源读取保护及通知收尾。

最终包：

- artifacts/packages/x64/HybridMount_0.0.10.0_x64_Test/HybridMount_0.0.10.0_x64.msix，SHA-256 `A448E5D8364749B38468B3014DB35C65F74C2750C17CD279E8B8360545274A9B`。
- artifacts/packages/ARM64/HybridMount_0.0.10.0_arm64_Test/HybridMount_0.0.10.0_arm64.msix，SHA-256 `210B99F96C38E838C4C1073C2DE8D5D7FC1282F2114BE6E1B5D2025FF49D54F4`。

使用已有当前用户Publisher证书签名；未新增证书或修改信任。最终包哈希与测试计数另存build-delivery-summary.json。

## 阶段失败、原因及修正

- 用户初始Debug x64日志：两个原生工程成功，主工程7个CS1061。SourceEntry的字段为StableId；7处协议调度后备上下文误用Id，已修正。命令行还原成功，未复现VS的VCProjectShim已释放异常，不能据此声称VS内部异常已消除。MUI输出覆盖提示不阻断构建。
- 首轮完整Debug为1726通过／25失败／1751总项，证据initial-Debug.trx及initial-Debug-tests.log。隔离宿主的OutDir／IntDir传给扩展依赖，同名ThumbnailCache等对象可被不同宏覆盖；修复引用属性移除并按工程分开对象目录，重新编译后冷提示和异步生成回归通过，没有清空用户缓存。
- 默认并发从2改为4、元数据与下载分道、快照先等待文件工作流后，旧断言不再适用。改为显式2路验证全局所有权、元数据独立执行、不同文件在通道等待、同路径远端应用等待文件屏障；未通过放宽正文或身份保护消除失败。观察器探针同步补齐显式根参数，原生菜单基准改为xUnit支持超时的异步任务。
- 原生协调入口强制MTA使既有STA调用方返回RPC_E_CHANGED_MODE；保留调用方已有公寓，仅平衡本次成功初始化，工厂仍由独立STA／MTA持有。真实类对象、回调中停止与后台通知生存期回归最终通过。
- 回执后同名替换因原身份限制不能取得内容证明，可能停在RemoteCommitted。新实现对完整当前对象做锁外摘要，并在原生保护中精确重验当前身份／戳；同正文的新身份不重复发送，真实新正文保存旧回执后进入下一代。WebDAV和S3、同／异正文四种替换最终通过。
- 第二轮定向Debug为182通过／1失败／183总项，证据targeted-Debug-before-localization.trx及同名log。失败为历史展示模板RegexMatchTimeoutException进入任务保存；增加固定文本前后缀预筛选及超时保留原文，增加受控超时回归。最终Debug全量及Release相关结果以上表为准。

- 再次完整Debug为1753通过／1失败／1754总项，证据full-Debug-before-notification.trx及同名log。通知计时器略早于到期时间唤醒后会耗尽信号并无限等待下一提示，导致缓存已发布但缺少自动刷新。修复为在锁外继续等待剩余到期时间，并按最早到期项处理；增加仅一次提示、强制到期晚于首次唤醒的回归。通知与语言定向26/26通过，最终完整结果见上表。

- 后一轮完整Debug为1753通过／2失败／1755总项，证据full-Debug-before-fixture-drain.trx及同名log。一项旧夹具在任务页后台快照尚未读完时删除临时数据库，已在Dispose后异步等待SnapshotIdle；另一项1100次慢解码探针的25秒外层期限未给采样间隔、元数据查询及退出收尾足够余量，改为60秒并在finally清理自建探针及子进程。原生解码期限、单次回调性能和P95／P99断言不变，实际探针总耗时保存于最终TRX。最终专项结果见上表，最终完整Debug覆盖修复后的夹具。

- 后续完整Debug为1752通过／3失败／1755总项，证据full-Debug-before-report-and-cache.trx及同名log。进度报告重命名在160ms重试窗内仍遇到外部读句柄；提示退避用例的自动扫描可先改变受测状态；另一个夹具的WAL被其他缓存统计读取者占用。分别调整测试报告为两秒有界重试、退避夹具先停止扫描后固定状态、测试缓存清理仅对32／33共享占用有界退让。测试集合上限固定为4，产品1—128与用例内部并发不变，未新增跳过或放宽产品保护。最终稳定化专项Release 23/23通过，最终完整Debug按该配置执行。

- 使用4路集合配置后，完整Debug为1754通过／1失败／1755总项，证据full-Debug-before-local-commit-retry.trx及同名log。剩余为实际产品问题：成功远端回执后的本地访问拒绝写入Error，却没有NextAttemptUtc，任务可能一直停在RemoteCommitted。修复为仅对本地5／32／33错误在原预算内最多三次重验，保留回执／快照／代次且不重发正文；其他错误仍要求显式处理。新增预算与单次远端写入注入回归，连同容量恢复相关Release 40/40通过。最终完整回归包含修复和新增用例。

- 扩展Release曾为491通过／1失败／492总项，证据expanded-Release-before-protected-replacement.trx及同名log。夹具在原生解码仍持读取保护时原子替换文件，被正常拒绝；修复夹具为只对已知访问／共享拒绝有界等待，异常时也先等待探针收尾。产品读取保护未放宽，最终Release 492/492通过。

## 性能证据及仍待验收项

原生菜单和异步取图的隔离调用分位数、队列和阶段输出保存在performance-results.json、async-samples-final/及最终TRX。相关用例满足各自阈值；它们不等同于Explorer真实消息响应、普通目录切换增量或冷COM启动测量。

待验收：真实WinUI三语言／窄窗／键盘与设置模式隔离；Explorer未加载／关闭缩略图／开启三态对照、千文件／多窗口／快速切换与滚动；慢Shell／UIA提供程序与真实消息响应；冷COM启动单列；普通目录切换P95相对基线增量、Shell P95／P99和消息响应端到端目标；安装／升级／卸载注销、真实DSM、ARM64实机。没有安装新包或重启Explorer来补测这些项目；.33停止专项继续排除。
