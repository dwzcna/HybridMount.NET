# 0.0.9.49 验收记录

基线f40d99b（.48），继续codex/native-backend／当时的预览身份。本地受检补丁整合，不推送、不改动master。

## 来源和实现

- #33 39727a4cb885b20761689da45af20e12b1df4de1：集中两种部署模式实际Mounts定义键，恢复检查只读查询另一模式，不创建／迁移它的配置。
- #24 d3a7e54a19121464c4cbdceaf65a1081f98e22b1：构造仅建立“检查中”快照；每根共享后台核对任务，连接、凭据编辑、断开及任务收尾先等待核对再取得根锁。检查期间禁用编辑／连接动作，失败保留恢复入口。
- #25 d53109f6f594fd937750508e6034e5adb1a31463：新增、凭据保存及远端根编辑核验离开调用上下文；对话框先只读显示，异步结果回UI后复核生命周期。缓存回收统计在后台。

组合冲突同时保留ForceYielding和EnsureInspectionAsync，未覆盖.45预览或.47任务／依赖实现。

## 实际验证

证据artifacts/validation/00949-pr-merge/：x64 Debug非打包最小构建通过；最终相关Debug／Release各59/59（targeted-*.trx），包括双模式不存在键只读、页面状态、真实原生连接及无消息泵启动／新增／凭据保存、协议安全诊断。Native测试映像按本版重新构建。

仅精确运行FailedMountTests.AsyncRemoteRootCheckRejectsMissingMountAndCancellation，未运行整个类；未运行OrphanedMountRecoveryTests、ClearRemovalTests或.33停止专项。测试注册与缓存按精确归属审计，证据00949-test-registry-*、00949-test-cache-*。

未执行真实WinUI对话框／启动交互、正式安装及ARM64实机；系列最终构建／签名包在.55。未使用正式挂载内容或真实远端，不重启Explorer。旧配置／SQLite／公共IPC v2格式保持。
