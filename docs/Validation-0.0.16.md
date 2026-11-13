# 0.0.16.0—0.0.16.3 验收记录

按版本顺序本地提交，不推送。基线为0.0.15.0提交69ce9c8；每版源码先写版本化补丁、git apply --check和git apply，成功删除工作补丁，证据保存于artifacts/validation/。不操作正式挂载、不重启Explorer。

## 0.0.16.0

共享原生事件线程保留即时内存计数／代次检查，关闭业务入每挂载有界串行链；最多256项，同条目待执行关闭合并，关键事件封闭合并段。一条查询供固定及内容通知复用，根核验不在共享调用栈执行。

x64 Debug和Release非打包构建通过，1096键×3语言／8原生字符串×3检查通过。两配置针对性各34/34，0失败／0跳过；证据00160-work/debug-build-test.log、debug.trx、release-build-test.log、release.trx。NativeCloseDispatchTests验证阻塞一挂载消费者后万次关闭只保留在途与待执行两项，另一挂载可继续；改名前后两个关闭分别看到旧／新路径，固定和内容通知同快照，停止后不接纳新通知。既有NativeNotificationQueue、FileOperationFeedback与PinIntent用例覆盖实际隔离CFAPI及取消释放。

本版没有重跑.15完整694项矩阵；ARM64交叉编译及双架构签名包在最后.16.3集中执行，不能记作本版已通过。通知处理失败仍沿用旧升级策略，.16.3继续处理。真实生产事件风暴、Explorer响应、Windows11 21H2、DSM及ARM64实机待测。
