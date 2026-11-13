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
