# 0.0.16.5 验收记录

基线a446c0b。补丁与日志保存在artifacts/validation/00165-work/。

## 实现

七项清理均落实：删除没有生产者的open／close处理；删除11个无引用样式和三个主题各三项画刷及media命名空间；将历史全量预览和兼容远端入口收进测试宏；统一RenameOwnsUpload；SQS测试改用ObserveEventHintsAsync并删除兼容包装与Compile登记；三语言删除无引用密码提示；生产不再编入ExcludedRenameCheckpointForTests委托。

## 实际验证

- x64 Debug非打包编译通过：build-x64-debug.log。
- Debug／Release定向各41/41，失败0、跳过0：debug.trx、release.trx。覆盖ExcludedRenameJournalTests、SqsEventTests、NativeNotificationQueueTests、ThumbnailCacheStoreTests和PreviewPhaseDeadlineTests。用例使用独立日志、数据库、内存／本机HTTP源及隔离原生进程，不注册同步根或连接正式云端。
- 生产程序集检查确认ExcludedRenameCheckpointForTests、LegacyRemotePreviewsAllowed、BrowseDrivenPreviews、ObserveS3HintsAsync和RenamedUploadMissing符号均不再存在；详见production-symbols.json。这是元数据字符串辅助核对，不替代界面验收。
- x64／ARM64 Release自包含MSIX构建、既有CN=dwzcn证书签名及包检查通过：package-tests-x64-release.log、package-arm64-release.log。ARM64测试程序集交叉编译0警告／0错误，未在ARM64运行；tests-arm64-build.log。本地化1160键×3语言／8原生字符串×3。
- 本轮未发生失败测试；MUI生成器覆盖既有卫星文件的提示保留在原始日志中。git diff --check通过。

## 待测试与边界

真实WinUI主题／高对比度／DPI和Explorer交互、Windows11 21H2及ARM64设备未执行。旧预览测试入口仍编译可用，本轮没有使用会注册系统根的集成夹具；不把编译或纯缓存测试当成完整取图验收。未安装包、未重启Explorer、未操作正式挂载。本地提交，不推送。
