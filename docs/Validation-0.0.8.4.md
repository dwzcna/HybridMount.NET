# HybridMount 0.0.8.4 验收记录

2026-11-04，Windows x64，.NET SDK 10.0.401／VS 2026／Windows SDK 26100。保持三个工程、现有 SDK／IPC v2／SQLite v6／备份 v3，版本统一 0.0.8.4。预算 45–75 分钟，GUI／服务等待另计。

## 已完成

- 用户堆栈定位在只读重试退避的 Task.Delay；该等待在关联令牌取消时主动产生 TaskCanceledException，堆栈本身不能证明未处理异常或崩溃。
- 退避改为计时器／取消通知竞争普通结果，等待完成即释放两者；传输将正常停止映射为已取消任务，不返回伪成功或空 HTTP 响应。内部完成任务观察所有故障，避免遗失后台异常。
- 元数据、集合发现、REPORT、配额由各自所有者继续区分调用方取消和总期限；未关联 HTTP 取消仍为有界超时故障。只读次数、Range／条件／正文保持、严格预览预算和已发送写入未知结果规则不变。

## 已测试

| 范围 | 实际结果与证据（artifacts/validation） |
| --- | --- |
| 最小构建 | x64 Debug 非打包成功，0084-minimal.log |
| 针对性 | 71/71、0 失败／跳过，0084-targeted.trx／0084-targeted.log |
| 完整 x64 | Debug／Release 各 396/396，0 失败／跳过；0084-contracts-x64-Debug.trx／0084-contracts-x64-Release.trx，0084-debug-final.log／0084-release-final.log |
| 签名 MSIX | x64／ARM64 0.0.8.4 签名、清单／PE／资源／许可与自包含核验通过；0084-package-x64-final.log／0084-package-ARM64-final.log |
| ARM64 测试工程 | 交叉编译成功、0 警告／错误，0084-tests-arm64-final.log；未在 ARM64 实机运行 |

新增用例准确等待退避的计时和取消注册都已建立，再取消或等待期限；断言只有一次实际请求、调用方任务为 Canceled／期限为 Faulted，正常退避停止不触发首次取消异常。启动／手动连接停止小于 5 秒、无异常挂载／Error 日志、无同步根注册。原 TLS、正文期限、写入未知结果、严格预览等回归一并通过。

## 未完成

本版范围代码已实现并完成上述自动验证。

首轮 Debug 为 395/396：上传恢复测试在数据库已完成但后台快照删除尚未执行的窗口断言失败。单项复测通过（0084-upload-restart-isolated.trx），静态核对确认提交与快照回收并非原子步骤；测试改为等待工作器停止后核对清理，未放宽产品保护或吞掉删除错误。原失败保留为 0084-contracts-x64-Debug-first-failed.trx／0084-debug-first-failed.log，后续完整 Debug／Release 均通过。

产物：artifacts/packages/x64/HybridMount_0.0.8.4_x64_Test/HybridMount_0.0.8.4_x64.msix；ARM64 对应目录。签名证书 3FB1D78271BA8F6EDD3CE163F57D93D13FEB83C0。

## 待测试

- 原现场 Visual Studio 引发时中断、继续后的真实托盘／任务与进程退出；HttpClient／SDK 内部等待取消仍可触发首次异常，这是调试器观察，不应当作产品已经崩溃。
- GUI／Explorer、安装升级、ARM64 实机和真实非打包启动。为保留新增自动连接且未注册的用户挂载，本轮不启动正式产品或修改其设置；不访问已取消的 NAS。
- 原服务 TLS 根因、SQS、历史 SHELL-01 与审查待办维持独立边界。本轮为本机注入与回归，无真实服务写入。
