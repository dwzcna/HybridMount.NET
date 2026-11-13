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
