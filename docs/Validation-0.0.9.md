# HybridMount 0.0.9 验收记录

2026-11-04，Windows x64，.NET 10.0.401／VS 2026／Windows SDK 26100。开发及自动验证初始预算4–8小时，真实服务与桌面／实机等待另计。保留三个工程、现有 SDK、IPC v2及SQLite v6；产品0.0.9，程序集和MSIX为0.0.9.0。

## 已完成

- WebDAV自动识别开关与手动上限互斥，保留手动值；S3仅手动。新旧定义默认关闭识别且上限为空，不改变未配置限额挂载的上传行为。按挂载保存，GB/TB十进制精确换算，拒绝零、负数、指数、非整数字节及溢出。
- 自动DAV使用RFC4331可信可用量；手动DAV只读递归配置根，S3无前缀／delimiter地分页统计整桶当前对象，包含映射之外对象。完整成功才发布；错误／取消／重复页／溢出不采用部分值。一百万条目安全界限，不读取正文或水合占位。
- 每范围租约串行协调统计与本程序写入，全局两路统计；缓存五分钟，成功回执只调整一次且不延长核实时间。已发送未知记录阻止同范围继续扩容，恢复先只读核对。失败重试至少一分钟，手动重试或重新核实可立即触发。
- 所有上传和副本先核对原版本、旧长度和净增长，之后才允许辅助写入；待核实／不足持久为WaitingCapacity，不占上传槽。等长和缩小更新可继续；设置变化在发送前再次核对，在途请求继续保留回执和未知保护。
- 页面草稿及命令在MountsViewModel，列表／选择不写回，防重入和失败回滚；备份v4包含容量选项，v1–v3按原字段白名单生成并兼容读取，旧备份不能悄悄写入新容量字段。注册表定义版本10，SQLite保持v6。

## 已测试

证据均位于artifacts/validation，以下仅记录实际执行结果。

| 范围 | 实际结果及证据 |
| --- | --- |
| 最小编译 | x64 Debug非打包与实际WinUI XAML成功：009-base-build.log、009-ui-build.log |
| 分批针对性 | 基础30/30、上传28/28、兼容96/96、最后54/54，全部0失败／跳过；009-capacity-base.trx、009-capacity-upload.trx、009-capacity-compat.trx、009-capacity-final.trx。批次有重复覆盖，不相加 |
| 完整x64 | 最终Debug／Release各486/486通过，0失败／跳过：009-debug-final.log、009-contracts-x64-Debug-final.trx、009-release-full.log、009-contracts-x64-Release.trx；首次Debug也为486/486 |
| 双架构签名 | x64／ARM64的0.0.9.0 MSIX签名验证0错误／警告，版本、PE、资源、第三方许可、自包含及清单核对通过：009-package-x64.log、009-package-ARM64.log、009-validation-final.log |
| ARM64交叉编译 | 测试工程编译成功、0警告／错误：009-tests-arm64.log；未在ARM64硬件运行 |
| 结构与版本 | 197个Compile无重复，新增公共契约／模型／协调器各登记一次；SQLite持久化文件与HEAD一致，定义schema10及备份v4：009-structure-audit.json、009-compile-items.json |

自动化覆盖完整DAV递归、S3整桶多页和前缀外对象、重复分页、错误子目录、配额缺失、缓存过期和失败冷却；实际NTFS／CFAPI覆盖刚好上限／超一字节／零字节／缩小／再增长、两挂载不同前缀共用整桶、取消与重启、响应丢失、发送前修改设置、配额正文阻塞断开五秒内完成及重连。备份和UI模型覆盖v1–v4、中文密码、旧定义、导入零网络／零注册、逐挂载草稿、保存失败及停止后不保存。

## 真实服务与精确清理

仅从已授权的正式挂载读取定义与DPAPI，使用独立本地根／HKCU测试子键；普通自动化不会调用真实入口。没有修改正式挂载设置、批量上传旧改动、创建远端辅助目录或访问已取消NAS。容量统计委托完整真实源，本地投影仅包含本轮唯一文件，不能以投影索引冒充用量。

| 服务 | 实际结果及证据 |
| --- | --- |
| 坚果云 | 手动容量不足时文件不存在于远端，提高到刚好上限后由文件系统触发上传并读回SHA256；继续完成固定更新、重启零字节和删除不复活。report Phase=Completed、Failure=null，ExactRemoteFile与LocalSyncRoot均Completed；009-real-dav-d6b44dfb69ed4f0bb304e7935257442d/report.json |
| Cloudflare R2 | 整桶当前用量1184853字节（该次观测），先阻挡超限、刚好上限放行并核对SHA256；继续完成列表／HEAD精度核对、固定更新、重启零字节和删除不复活。report Phase=Completed、Failure=null，ExactRemoteFile与LocalRoot均Completed；009-real-s3-c4a248e709e34c5dba937a9933032e17/report.json |

两轮只操作报告中的hm009唯一文件；清理前验证内容属于本轮已知摘要，随后精确删除并通过公开clearAndRemove注销独立根，未留待清理测试入口。汇总日志009-live.log。真实服务经历网络等待，未将它混入自动化测试耗时。此记录不作为RFC6578或AWS SQS实服务认证。

## 失败与修正

- 首轮兼容编译发现导入预览空值流分析CS8604，补充明确非空校验后96/96通过，未降低警告等级。
- 首轮Debug完整构建在NuGet还原阶段遭TLS中断（NU1301），保留009-debug-restore-failed.log。未更改证书或杀毒设置；全部固定依赖已缓存，后续构建使用本机NuGet包源并关闭该次在线审计，包版本不变，成功还原／编译。此结果不证明NuGet网络故障已解决。

## 未完成

本版计划内代码和可执行自动／真实服务验收完成。GUI／Explorer、安装升级及ARM64实机仍属于待测边界。

产物：

- artifacts/packages/x64/HybridMount_0.0.9.0_x64_Test/HybridMount_0.0.9.0_x64.msix，SHA256 `34ebc87aa573256e7c7d3f1fd48bf30d968ec2a81ee946259f8d183b7e8684c7`。
- artifacts/packages/ARM64/HybridMount_0.0.9.0_arm64_Test/HybridMount_0.0.9.0_arm64.msix，SHA256 `088907dce48f120e5e184418d99bfdb85cfa22af94a1344e690ea715e038d351`。

签名证书3FB1D78271BA8F6EDD3CE163F57D93D13FEB83C0。产物不纳入源码提交。

## 待测试及限制

- 实际GUI、Explorer、安装升级、高DPI与ARM64实机。正式非打包进程启动未执行，保留自动连接但未注册的用户挂载现场；非打包构建通过。
- 手动容量是当前逻辑内容上限，不计历史版本、未完成分片、目录元数据或临时写入峰值；自动配额以服务器声明范围为准，不能当NAS物理容量。
- 外部客户端可在五分钟缓存窗口或上传检查之后写入；多请求扫描不是原子快照。需要严格全局配额时仍由服务端执行。S3整桶统计需要整桶列举权限，失败保持等待，不降为前缀估算。
- NAS 10.0.0.20测试保持取消。TEST-RUNNER-01、SHELL-01、旧TLS／调试器现场、真实RFC6578／AWS SQS及其他审查待办继续独立记录，不扩大本次范围。
