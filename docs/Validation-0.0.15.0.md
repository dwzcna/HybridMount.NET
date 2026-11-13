# 0.0.15.0 验收记录

基线master / 1af8c5b（0.0.14.0）；证据 artifacts/validation/00150-work/。自动验证与构建已完成；实际设备、生产与人工待测单列，不把阶段失败或中止矩阵记为通过。验证记录后本地提交，不推送。

## 实现

见TaskPipeline-0.0.15.0.md。四工程、安装身份、公共IPC v2、.hmxbk JSON及加密保持；私有运行时v2和新本机索引格式7同版匹配，旧索引只读拒绝。原生监听覆盖连接根，托管两组FileSystemWatcher已移除；执行入口通过四层服务及安全适配器，上传有快照、云核验及本地提交子步骤。

## 阶段自动回归

| 阶段 | 实际结果 | 证据 |
| --- | --- | --- |
| 首轮监听／任务管线Debug | 60/60 | pipeline-debug-initial.trx |
| 扩大Debug | 675通过／14失败 | adapters-debug.trx |
| 定向修正后Debug | 42/42 | adapters-debug-retry2.trx |
| 扩展Debug后续 | 689通过／1失败，共690 | adapters-debug-final.trx |
| 停止监听修正定向Debug | 23/23 | removal-debug-18.trx |
| 退役协议修正定向Release | 36/36 | retirement-release.trx |
| 最新完整相关Release | 694/694，0失败／0跳过 | adapters-release-final3.trx |
| 最新Debug定向及主程序／隔离宿主构建 | 66/66，0失败／0跳过 | debug-final.trx、debug-final-build-test.log |

最后Release矩阵覆盖最新产品源码，持续18分54秒，隔离目录持续推进；最长用例为861条积压46秒、检查点31秒。Debug最终为定向复验，不声称此前690项完整重跑。Release暖态各100样本：1,000／10,000项任务快照固定5次SQL，P95=21.434／462.380ms，P99=26.067／551.805ms，低于150／750ms目标；这是数据库快照成本，不能替代真实UI响应。覆盖持久意图／重发／缺口、步骤依赖与代次、旧格式拒绝、真实IOCP及改名／溢出／万次事件／外部联接、WebDAV／S3／群晖及下载、改名、固定释放等安全流程。

## 实际独立 WinUI

RestrictedUi引用真实产品App、MainWindow和SettingsPage，偏好、语言、启动、日志使用隔离替身；不运行产品主入口，不连接正式挂载。restricted-ui.json记录24组通过：三语言×明暗×旧格式／较新格式／损坏／读取失败；实际ContentRoot画刷Alpha=255，设置可达，关闭后仍拒绝业务导航，原生运行时未创建。操作按钮验证设置入口，未点击真实重置。

首次验证程序未安装DispatcherQueue同步上下文，异步续接错误线程产生8001010E；修正验证程序后最终JSON通过。旧restricted-ui-error.txt、restricted-ui-cleanup.txt保留为早期失败，时间早于最终通过JSON。产品窗口的通知容器显隐同时纳入索引InfoBar，防止有提示但外层被折叠。

## 失败与修正

- 首轮扩大回归发现重试被通用完成记录拦截、关闭Pin维护时上传路由误停、改名关联晚于缺失扫描及已清除历史的持久问题漏显，均修正并定向复验。
- 类型改变的旧尝试改为允许Sending／OutcomeUnknown阶段竞争，先核对已发结果；在恢复摘要前核对类型，保留回执、原本地与快照。
- 861条积压需额外任务日志导致耗时超界，重复观测改为256项事务核验，不创建新的文件步骤；原生意图也按256项共用扫描，避免重新引入逐项全表访问。
- 一轮大目录夹具清理原生查询失败；删除／注销前先停止监听并交付事件，随后定向Debug23项通过。夹具失败诊断保留精确相对路径，未跳过占位保护。
- 首轮Release编译的内部构造可见性错误修正后继续。随后发现停止退役消息使用GUID_NULL，被原生协议拒绝并关闭共享运行时；改为非空消息ID，补退役后通道仍可用的回归。确认该原因后主动终止失效矩阵，保留中止TRX（已执行534项：414通过／120失败），不把它当完整运行或产品崩溃复现。
- 中止留下83个已注销测试根：严格限定为该轮03:36:54—03:42:57创建的Temp/HybridMount.HttpTests/<随机GUID>目录，核对当前用户、根ID及原生身份后注销，保留文件／数据库作证据。aborted-test-roots.json、aborted-test-cleanup.json记录83/83；未操作D:\myweb、D:\wdavtest或其他正式挂载，未重启Explorer。

## 构建、签名与设备

最终x64 Debug非打包及定向回归、x64 Release非打包、x64／ARM64 Release签名MSIX与包检查均通过；1096键×3语言／8原生字符串×3检查通过。ARM64测试程序集交叉编译0警告／0错误，未执行ARM64测试。签名使用既有CN=dwzcn证书，未安装正式包、未新增证书信任、不推送。

证据：debug-final-build-test.log、unpackaged-release-final.log、package-x64-final.log、package-arm64-final.log、tests-arm64-cross-final.log。MSIX SHA256：

- x64：`33242948FB1F04F825D6967CFEB3A386BDAD10807537E5061F916530B5F7352F`
- ARM64：`86A5D7EF45E1A7D369CAEB5AF6D830A18BB91A16DE4F2860AD908C1DF9174851`

真实Office覆盖保存、进程崩溃／断电后的全链恢复、长时断线重发、当前模式实际重置、高对比度／DPI／键盘／读屏、真实任务详情与Explorer交互、生产规模、Windows11 21H2、DSM和ARM64实机仍待测。没有补跑.33停止专项；ARM64交叉编译不等于设备运行。
