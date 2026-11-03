# HybridMount 0.0.8.11 验收记录

2026-11-04，Windows x64，.NET 10.0.401／VS 2026／Windows SDK 26100。范围为外观设置服务合并，完成本批 0.0.8.7—11；原开发及分版自动验证预算 8–14 小时，真实 GUI／服务及实机等待另计。三个工程、配置语义、HKCU 根、SQLite v6、协议、IPC v2 和备份 v3 保持。

## 已完成

- AppearancePreferenceStoreService.cs 移除，默认 RegistryAppearancePreferenceStore 在 AppearanceSettingsService.cs 内部实现；生产统一 new AppearanceSettingsService()，保留 IAppearancePreferenceStore 注入及隔离注册表回归入口。
- 原 AppearanceBackdrop／AppearanceTheme DWORD、AppearanceSettings 旧值清理、SchemaVersion 及根路径保持。读取仍只读，各字段独立校验，初次读取失败不能保存默认值；成对保存、恢复原值、错误提示及锁外通知代码保持。
- SettingsViewModel 继续拥有外观页面状态和重入防护，成功即时应用、失败恢复选择；释放解除订阅，导航离页保持缓存状态。

## 已测试

| 范围 | 实际结果与证据（artifacts/validation） |
| --- | --- |
| 最小非打包 | x64 Debug 产品编译成功，00811-minimal.log |
| 针对性 | 外观、状态、导航、原契约 52/52，0 失败／跳过；00811-targeted.trx／.log |
| 完整 x64 | Debug／Release 各 458/458，0 失败／跳过；00811-contracts-x64-Debug.trx／Release.trx，00811-debug-final.log／release-final.log |
| 签名与交叉编译 | x64／ARM64 0.0.8.11 签名 MSIX 的版本／PE／清单／资源／许可与自包含检查通过；00811-package-x64-final.log／ARM64-final.log。ARM64 测试仅交叉编译，00811-tests-arm64-final.log |
| 结构与保全 | 190 Compile 无重复，统一外观服务一次，旧存储文件和编译引用移除；服务 Current／Reload／TrySave／Notify 核心与注册表 Read／Write 内容完全一致，00811-structure-audit.json、00811-compile-items.json |

新增六项回归分别验证：真实隔离 HKCU 首次读取不创建键、旧键名与重启恢复及不改启动／托盘偏好；非法类型／枚举逐字段回退且不改原值或 schema；注入读取失败不写默认值、恢复后读回原配置；成功／失败及故障订阅的精确通知次数；VM 重建、重入和释放后不通知；默认存储归属与生产唯一入口。既有实际注册表契约改为通过统一服务执行，不再直接创建旧存储类型。

核心 SHA-256 为 28fecc4f060ffb00116d6e3b46344afe24811ded6ff583ecc263474d79cc6ae5，存储读写为 8ebe52671dd19293d260b77fad35bccfffc895467dbcff16be293765e28dfc27；两者与合并前对应代码一致。本轮测试只使用独立 HKCU 测试子键和注入存储，没有读取或修改正式外观配置，也未启动正式进程或连接真实远端。

## 未完成

本版代码范围完成。产物：artifacts/packages/x64/HybridMount_0.0.8.11_x64_Test/HybridMount_0.0.8.11_x64.msix；ARM64 对应目录。签名证书 3FB1D78271BA8F6EDD3CE163F57D93D13FEB83C0。

## 待测试

- 实际 GUI 的外观即时切换、系统主题／高对比度／材质回退、页面与滚动、系统启动设置和真正退出；Explorer、跨版本升级与 ARM64 实机。
- 正式非打包进程启动未执行，保留自动连接而未注册的用户挂载现场；非打包构建已通过，测试没有为启动验收更改正式挂载。
- 0.0.8.8 已完成坚果云／R2 唯一文件真实闭环并精确清理；该记录不作为本版新测试或真实 RFC 6578／AWS SQS 认证。已取消 NAS 不访问，TEST-RUNNER-01、SHELL-01、原 TLS／调试器现场及其他审查待办保留。
