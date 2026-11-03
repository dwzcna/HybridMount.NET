# HybridMount 0.0.8.5 验收记录

2026-11-04，Windows x64，.NET SDK 10.0.401／VS 2026／Windows SDK 26100。版本统一 0.0.8.5，保留三个工程、SDK、IPC v2、SQLite v6 与备份 v3。开发验证预算 30–60 分钟，GUI／服务等待另计。

## 已完成

- 任务页增加清除成功历史按钮；无 Completed 时禁用，执行时防重复点击，后台完成后刷新列表并反馈条数。切换页面不取消已接纳清理，退出按应用所有权等待。
- 单条原子 SQL 清除整个 operations 表内的 Completed，保留 Failed、Canceled、PartialFailure、Conflict、OutcomeUnknown 及所有当前工作；排除持久记录已进入非完成状态的同 ID 上传或远端恢复。
- 仅清理展示历史；文件、远端数据、上传记录、内容基线、索引和恢复快照不删除。后续本地编辑沿用原基线，已清除的上一代完成历史不重新创建，新代任务正常出现。

## 已测试

| 范围 | 实际结果与证据（artifacts/validation） |
| --- | --- |
| 最小构建 | x64 Debug 非打包成功，0085-minimal.log |
| 针对性 | 47/47、0 失败／跳过；0085-targeted.trx／0085-targeted.log，含新增 9 项清理回归及上传一致性／配额／任务排序 |
| 完整 x64 | Debug／Release 各 405/405，0 失败／跳过；0085-contracts-x64-Debug.trx／0085-contracts-x64-Release.trx，0085-debug-final.log／0085-release-final.log |
| 签名 MSIX | x64／ARM64 0.0.8.5 的签名、版本／PE／清单／许可／资源与自包含核验通过；0085-package-x64-final.log／0085-package-ARM64-final.log |
| ARM64 测试工程 | 交叉编译通过、0 警告／错误；0085-tests-arm64-final.log，未在 ARM64 实机执行 |

覆盖 150 条成功历史跨越列表上限、数据库重开、13 种其他状态保留、已完成上传的回执／原始版本／摘要和索引保留；新代不重建旧历史。四类未完成上传和 Hydrating 恢复记录阻止清除旧的 Completed 展示行。20 次并发重新入队可见、停止后拒绝迟到清理，以及真实 NTFS／CFAPI 本地回归任务不会被内存覆盖重新加入列表。

## 未完成

本版范围代码已实现并完成上述自动验证。产物：artifacts/packages/x64/HybridMount_0.0.8.5_x64_Test/HybridMount_0.0.8.5_x64.msix；ARM64 对应目录。签名证书 3FB1D78271BA8F6EDD3CE163F57D93D13FEB83C0。

首轮 Debug 为 404/405，新增清理回归均通过，既有本机长测器的 remote delete cleans pinned file 失败；原记录保留为 0085-contracts-x64-Debug-first-failed.trx／0085-debug-first-failed.log。未在该路径调用清除历史，随后单独诊断 2/2 通过（0085-runner-diagnostic.trx／.log）。测试增加失败报告内容以便下次定位，未放宽断言或产品保护；TEST-RUNNER-01 仍待确定性交错和根因，不将复测通过写成该问题已经修复。

## 待测试

- 实际 WinUI 按钮视觉、清除中的连续点击与导航、选择／滚动恢复；安装升级和 ARM64 实机。
- 为保留新增自动连接且未注册的用户挂载，本轮不启动正式产品或改配置。原服务 TLS、调试器取消现场、真实 SQS 与历史 Shell／审查待办沿用各自边界；不访问用户取消的 NAS。
- 本轮后端回归仅使用本机隔离测试根和服务器，不清除正式任务或写入用户远端。
