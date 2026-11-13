# 0.0.16.0—0.0.16.3 验收记录

按版本顺序本地提交，不推送。基线为0.0.15.0提交69ce9c8；每版源码先写版本化补丁、git apply --check和git apply，成功删除工作补丁，证据保存于artifacts/validation/。不操作正式挂载、不重启Explorer。

## 0.0.16.0

共享原生事件线程保留即时内存计数／代次检查，关闭业务入每挂载有界串行链；最多256项，同条目待执行关闭合并，关键事件封闭合并段。一条查询供固定及内容通知复用，根核验不在共享调用栈执行。

x64 Debug和Release非打包构建通过，1096键×3语言／8原生字符串×3检查通过。两配置针对性各34/34，0失败／0跳过；证据00160-work/debug-build-test.log、debug.trx、release-build-test.log、release.trx。NativeCloseDispatchTests验证阻塞一挂载消费者后万次关闭只保留在途与待执行两项，另一挂载可继续；改名前后两个关闭分别看到旧／新路径，固定和内容通知同快照，停止后不接纳新通知。既有NativeNotificationQueue、FileOperationFeedback与PinIntent用例覆盖实际隔离CFAPI及取消释放。

本版没有重跑.15完整694项矩阵；ARM64交叉编译及双架构签名包在最后.16.3集中执行，不能记作本版已通过。通知处理失败仍沿用旧升级策略，.16.3继续处理。真实生产事件风暴、Explorer响应、Windows11 21H2、DSM及ARM64实机待测。

## 0.0.16.1

FileTask和文件任务重试按FindOperation主键点查；仅恢复同MountId／条目／操作的Running、Deferred、Interrupted。原版本和文件身份不被新值覆盖，释放执行器仍先比较并在变化时更新原尝试为Failed。批量LoadFileOperations保留每轮一次读取，新增operations_file_mount表达式索引；未引入长驻连接或表格式变更。

最终x64 Debug／Release非打包构建及针对性各33/33，0失败／0跳过。00161-work/debug-final-build.log、debug-final.log／trx、release-build-test.log／release.trx记录结果；1096键×3语言检查通过。10,001行夹具连续256次恢复只读取目标记录，其他行包含无法反序列化为业务对象的数据，未被读取。EXPLAIN QUERY PLAN实际为`SEARCH operations USING INDEX sqlite_autoindex_operations_1 (id=?)`及`SEARCH operations USING INDEX operations_file_mount (<expr>=?)`。

首轮28通过／5失败：4项为集合数组／列表引用相等断言，改比较持久内容；另1项为过度筛选旧版本导致释放尝试ID被替换，修正为保留证据交原核验器处理。debug-initial.trx及debug-build-test.log保留失败，最终两配置33项均复验。未重复.15完整矩阵；ARM64与签名包仍在最终.16.3集中执行，生产／设备待测不变。

## 0.0.16.2

remote_pending的本地映射屏障使用HM_PATH，自定义比较委托为.NET OrdinalIgnoreCase；独立新增索引，未覆盖SQLite内置NOCASE，不修改S3原始键／提示／水印的精确身份。主查询、祖先及必要子树、相关上传／删除祖先与任务展示共用路径规则；取消纯元数据不阻塞，取消正文仍保护。根删除等待全部子项，空根的上传关联查询覆盖整挂载；%与_按字面匹配。新连接和只读格式预检都注册排序，格式7及.hmxbk保持。

x64 Debug／Release主矩阵各65/65，0失败／0跳过；00162-work/debug-final.log／trx、release-build-test.log／release.trx。最后根子树关联补充以两配置各13/13及双配置最终非打包构建覆盖，证据debug-related-final*、release-related-final*，主65项不是该末次局部修改后的完整重跑。1096键×3语言检查通过。

RemotePathBarrierTests涵盖ASCII／拉丁／希腊／西里尔大小写、跨作用域、根及兄弟、取消与失败状态、S3原始键和本地别名、重启只读预检。查询计划实际命中`remote_pending_path_ordinal (mount=? AND path=?)`，没有扫描remote_pending。Release任务快照暖态100样本P95=8.613／344.828ms（1,000／10,000项），固定5查询；不能替代真实UI响应。

首轮64通过／1失败：根删除SQL的空字符串引用不完整，静态修正晚于首轮编译；改为长度谓词后最终65项双配置均通过。debug-initial.trx及首轮日志保留。未重跑.15完整694项；ARM64／签名包在.16.3执行，真实协议设备与生产待测。

## 0.0.16.3

合法业务通知最多四次执行，退避100／500／2000ms，不持根锁或云执行位等待。SQLite忙／锁／I/O等可重试，结构损坏及无法继续的业务失败只隔离当前挂载；队列超过256项同样停止受影响会话，不能丢弃后继续。管理器取消该挂载并保留持久意图，重连按既有全量补扫和未决结果核验恢复。字段、身份及路径越界属于不可信协议，仍关闭共享运行时。

改名／删除子树同一事务保存，注入提交前失败全部回滚；提交后订阅者重试复用原通知。独立任务管道已推进索引时，迟到CFAPI改名只请求补扫，不能回写旧路径或错误断开。类型替换取消等待不代表服务器取消修改，远端暂不存在时保留原OutcomeUnknown、快照和错误证据；出现迟到目录回执后保存回执并维持真实类型冲突，不重新发送修改。结果长期不明仍需用户核对，不推定成功或回滚。

Windows SDK shared/winerror.h的ERROR_CLOUD_FILE_REQUEST_CANCELED为398，即HRESULT=0x8007018E，含义为用户取消云操作。两端完成分类均识别该终态，真实未知完成仍保留安全关闭。原生EntryId上限对齐4096字节，超过范围仍拒绝，不因新校验缩窄旧契约。通知只读诊断记录积压、重试、隔离和补扫请求，不触发文件正文或生成。

| 阶段 | 真实结果 | 00163-work证据 |
| --- | --- | --- |
| 首组故障／通知Debug | 49/49 | debug-build-test-final.log、debug.trx |
| 追加索引损坏／提交后订阅者Debug | 12/12 | debug-failure-final.log／trx |
| 首轮扩大Release | 154通过／2失败，共156 | release-build-test.log、release-initial.trx |
| 迟到观察／回执修正定向Release | 18/18 | release-late-fix.log／trx |
| 修正后主要Debug | 58/58 | debug-latest-build-test.log、debug-latest.trx |
| 再次扩大Release | 155通过／3失败，共158 | release-final-build-test.log、release-cancel-before-fix.trx |
| 系统取消分类修正Debug | 58/58 | debug-cancel-final-build-test.log、debug-cancel-final.trx |
| 取消修正后扩大Release | 190/190，0失败／0跳过 | release-cancel-final-build-test.log、release-cancel-final.trx |
| 最终通知身份预算对齐 | Debug16/16、Release16/16 | debug-identity-final.log／trx、release-identity-final.log／trx |

最后190项矩阵覆盖通知、真实原生CFAPI、读取优化、上传、连续排除改名、释放、文件恢复及路径屏障；末次仅调整ID预算并补边界用例，用两配置各16项覆盖，没有把190项算作末次局部修改后的完整重跑。没有执行.15完整694项矩阵或.33停止专项。

首轮编译发现InvalidDataException并非IOException子类的模式写法错误，修正后才运行测试。首轮两项失败分别是迟到改名误隔离及类型替换提前终结结果，保留严格断言后修复；再次扩大三项失败由实际0x8007018E正常取消触发共享运行时误关闭，原生／托管同步修正后190项通过。两轮失败时间窗的HKLM注册审计仅选择随机HttpTests／S3Tests临时目录、当前用户与部署模式，候选0（failed-matrix-roots.json），未注销任何额外根或删除文件。

### 最终构建与包

最终x64 Debug／Release非打包构建通过（debug-identity-final-build.log、unpackaged-release-final.log）；x64／ARM64 Release签名MSIX与包检查通过（package-x64-final.log、package-arm64-final.log）。1096键×3语言／8原生字符串×3检查通过。ARM64测试程序集交叉编译0警告／0错误（tests-arm64-cross-final.log），没有在ARM64设备执行测试。签名使用既有CN=dwzcn证书，不安装产品包或增加信任。

- x64：`artifacts/packages/x64/HybridMount.NET_0.0.16.3_x64_Test/HybridMount.NET_0.0.16.3_x64.msix`；SHA256：`4267158212D856760CC560CF7026F6A2B02B437660FDD90D99805BC6FC60897C`。
- ARM64：`artifacts/packages/ARM64/HybridMount.NET_0.0.16.3_ARM64_Test/HybridMount.NET_0.0.16.3_ARM64.msix`；SHA256：`2FE2E712ACB4093146DA77569D84873EA38E5F523FFCA800133D06AAD4636CA7`。

最终路径／哈希另存final-packages.json。保留四工程和安装身份、格式7、.hmxbk JSON与加密、公共IPC v2；仅测试构建有故障注入及分类探针。全部修改经版本化补丁检查与应用，记录后本地提交，不推送、不操作正式挂载、不重启Explorer。

真实Explorer／WinUI端到端响应、生产文件与事件规模、真实慢盘／磁盘故障、断电及长时断线、Windows11 21H2、DSM及ARM64实机仍待验收。隔离成功不等于这些项目已通过。
