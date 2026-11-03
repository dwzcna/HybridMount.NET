# HybridMount 0.0.8.7 验收记录

2026-11-04，Windows x64，.NET SDK 10.0.401／VS 2026／Windows SDK 26100。本批 0.0.8.7—11 开发验证预算 8–14 小时，逐版交付，真实服务／GUI 等待另计。本版统一 0.0.8.7，三个工程、SQLite v6、配置字段／注册表根、DPAPI、IPC v2 与备份 v3 保持。

## 已完成

- 公共领域、系统交互、持久化及协调分开；65 个原文件逐一归属及实际逻辑拆分见 Mounting-structure-0.0.8.7.md。
- UploadRules 与 MountDefinitionRules 集中纯校验和状态规则；定义仓库注入、SQLite 初始化独立、上传提交注入默认 CFAPI 适配器。保护句柄、稳定戳、事务、远端删除屏障和未知写入保护保持。
- 诊断 DTO／契约与指针复制分开；路径算法与兼容入口分开，预览／传输／文件系统服务按职责归组。协议专用代码下版迁移，本版不重复移动。

## 已测试

| 范围 | 实际结果与证据（artifacts/validation） |
| --- | --- |
| 最小构建 | x64 Debug 非打包成功，0087-minimal.log |
| 针对性 | 115/115、0 失败／跳过，0087-targeted.trx／.log |
| 完整 x64 | Debug／Release 各 411/411，0 失败／跳过；0087-contracts-x64-Debug.trx／0087-contracts-x64-Release.trx，0087-debug-final.log／0087-release-final.log |
| 签名与交叉编译 | x64／ARM64 0.0.8.7 签名 MSIX 的签名／版本／PE／清单／资源／许可及自包含通过；0087-package-x64-final.log／0087-package-ARM64-final.log；ARM64 测试交叉编译 0 警告／错误，0087-tests-arm64-final.log |
| 结构与持久化 | 155 个 Compile、84 个挂载文件各一次，0 重复／遗漏；原建表 SQL 字节相同，SHA-256 d8d1238b7c7e5cc0f9a4ba8314c0ceac0ca58ec9bc3e095f76295564ad7ce7af；0087-structure-audit.json／0087-compile-items.json |

注入测试在读取失败后不回写、保存失败保留原定义，真实隔离 NTFS／CFAPI 上传通过注入提交适配器完成；新增职责边界验证模型无 Services／PInvoke 依赖。既有上传提交 F01／类型 F02、读取合并／范围、配置备份及停止／移除／远端同步在本机模拟服务器与原生测试根执行。

## 未完成

本版范围代码已实现并通过上述自动验证。65 个原文件中 13 个发生实际职责提取，协议专用文件内容完全未动：0087-ownership-audit.json。

首轮 Debug 410/411，旧缩略图保存失败测试反射访问已迁出的 _registry 字段而抛出 NullReferenceException；原失败保留为 0087-contracts-x64-Debug-first-failed.trx／0087-debug-first-failed.log。改用 IMountDefinitionStore 注入写入故障，仍断言内存和持久设置保留原值，7/7 边界复测通过（0087-injection-fixed.trx／.log），随后 Debug／Release 全量均通过，未恢复私有字段或放宽断言。

产物：artifacts/packages/x64/HybridMount_0.0.8.7_x64_Test/HybridMount_0.0.8.7_x64.msix；ARM64 对应目录。签名证书 3FB1D78271BA8F6EDD3CE163F57D93D13FEB83C0。

## 待测试

- 本轮未访问真实服务，没有用模拟服务器代替真实 RFC 6578／SQS 兼容性认证；用户取消的 NAS 不访问。
- GUI／Explorer、安装升级、ARM64 实机和正式非打包启动。保留当前自动连接但未注册的用户挂载，未为验证更改正式配置或启动可能注册它的产品实例。
- TEST-RUNNER-01、SHELL-01、原 TLS／调试器现场与审查项继续单列；交叉编译及签名检查不冒充实机执行。
