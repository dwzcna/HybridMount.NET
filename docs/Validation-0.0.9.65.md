# 0.0.9.65 CFAPI通知有界等待验收

## 实现与证据边界

基于c89fe1d。原CloudSession::Event默认调用Host.Send(...,true)，容量满时OutWake.wait没有期限；这证明源码等待风险，没有把历史Explorer卡顿归因于它或声称已实机复现。

通知使用独立邮箱和发送通路，普通控制回复仍在原队列。关键事件最多256项／2MiB；活动状态最多128项／512KiB，不能借用关键字节预算。最外层回调建立45ms排队预算，重入及一个取消区间内的多次通知共用剩余时间。已有FETCH_DATA仍非阻塞，容量不足结束该驱动读取。通知失败只置位原子故障并唤醒独立监视线程，监视线程在回调外封闭通道、取消和唤醒拥有者；停止唤醒不依赖发送或业务消费者释放槽位，不注销持久根。

打开／关闭改为绝对打开数、累计关闭数和递增序号。同连接同条目的未消费状态取最新，旧序号不回退计数，关键事件阻断跨事件合并，关闭发生后再打开也保留关闭累计用于触发核对。托管邮箱关键256项／4MiB，状态128项／1MiB；业务通知再以256项上限串行应用，保持改名／删除因果。停止前已接纳项排空，停止后及旧代次不接纳新状态。驱动完成移出会话状态锁，保留既有可重入回调票据和断开排空。

只读计数包括待处理、合并／拒绝／交付、排队累计／最大时间、回调总数／超过50ms／最大耗时；应用诊断追加内存邮箱摘要。45ms为排队预算，实际回调耗时另行采样，系统调度与CFAPI耗时仍须实机验收。配置、SQLite、公共IPC v2、四工程、HybridMount.exe和NativePreview身份保持。

## 实际验证

证据目录：artifacts/validation/00965-notifications/。

| 项目 | 结果 | 证据 |
| --- | --- | --- |
| 最终x64 Debug相关 | 155/155，0跳过 | delivery-final-Debug.trx |
| x64 Release主矩阵 | 154/154，0跳过；当时尚未包含最后追加的外部进程用例 | delivery-Release.trx |
| 最新通知／真实CFAPI双配置 | 各14/14，包含外部进程用例 | final-budgets-and-pairing-Debug.trx、final-pairing-recompiled-Release.trx |
| 共享占用与通知专项 | 连续三轮各10/10 | sharing-and-queue-1—3-Debug.trx |
| x64双配置非打包 | 通过 | delivery-build-x64-*.log |
| x64／ARM64 Release签名MSIX／包检查 | 通过 | delivery-package-*.log |
| ARM64测试工程 | 仅交叉编译通过 | tests-ARM64-Release-compile.log |

隔离原生邮箱同时保留128状态和256关键事件，满后拒绝，测得排队退出47ms；关闭唤醒16ms。此为单次注入样本，不是P95/P99统计。纯内存状态10000次合并保留最新打开数／关闭累计，关键改名位于前后状态之间，旧序号不能覆盖新值；大状态耗尽自身字节预算仍可接纳全部关键槽位。

真实外部PowerShell子进程保持本轮独立NTFS根内完整文件的读取句柄，收到openCount>0的活动通知；释放后观察closeCount=1、openCount=0、sequence=2。源请求数不变，文件和占位身份、Pin、InSync保持；仅操作本轮临时根，子进程隐式水合未发生。真实CFAPI读取样本记录3个回调、未出现超过50ms样本；并验证断开等待回调票据、故障唤醒、取消、关键通知串行排空和不重启消费者即可停止。数值见timing-and-pairing.json及TRX标准输出。

## 阶段记录与限制

- 初轮相关91/91通过。扩大矩阵首次150/151，一项S3排除改名在本地收尾遇到共享占用并安全保留内容；没有证据确定占用方，也未为使测试通过放宽文件共享或保护。之后该场景与通知专项连续三轮10/10，最终Debug和Release矩阵均覆盖通过；原delivery-first-Debug.trx保留，现场共享占用来源仍待核查。
- 后续补强数量和字节预算隔离、已接纳通知停止排空及真实外部打开关闭。Debug补强14/14后重跑完整相关155/155。Release主矩阵154/154已经覆盖预算改造，最后追加的外部进程用例最初未出现在增量发现列表；对测试工程显式Rebuild后最新14/14，不将旧列表当作新增用例已执行。
- 不执行正式挂载、正式应用升级或Explorer重启。人工Explorer长时间操作、实际过滤驱动／慢消费者和高负载下的完整回调响应、正式升级及ARM64实机待测。未重跑.33停止专项。

## 产物

D:/src/HybridMount.NET/artifacts/packages/ARM64/HybridMount_0.0.9.65_arm64_Test/HybridMount_0.0.9.65_arm64.msix

SHA256：4CD2F21ACE88AFB32888DCF2A2AACD8226EDAF3AF3ADC5E3DC1E91B50F5BF679

D:/src/HybridMount.NET/artifacts/packages/x64/HybridMount_0.0.9.65_x64_Test/HybridMount_0.0.9.65_x64.msix

SHA256：05D9046C4EAA7029A604D6BB40EC23299EF6E9C6B2588B643C7C192145A9A517

本版本仅本地提交，不推送或修改master；继续按已确认顺序实施.66—.68。
