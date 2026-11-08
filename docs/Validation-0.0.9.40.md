# HybridMount 0.0.9.40 验收记录

起点0f09d38（0.0.9.39已本地提交，包含用户原暂存），仅三个工程及Debug／Release配置；不推送，不继续0.0.9.33已停止专项。代码和自动验证预算4—6小时，外部等待另计。

## 已定位链路

TasksPage对话框 → TasksViewModel捕获比较 → MountManager比较本地元数据及远端版本 → 取得远端Serial及根锁 → 等待上传／属性路径所有权 → 持久准备与本地备份 → 条件上传或受保护本地替换 → 原子退休精确事件。

旧实现遇同挂载其他上传即拒绝，VM随后使比较失效，用户需要重新核对与选择。另一来源是问题图标发布更新Attention和本地USN／ChangeTime，整个记录及状态戳相等校验把本程序元数据更新误当正文变化。

## 第一批实现及证据

新增可取消的优先等待与独立状态广播；等待不持根锁、不启动额外上传。工作器在路径被预留时等待事件，不忙循环。改名收尾也遵守相同预留，取消／断开使用原生命周期令牌。

仅CloudUploadIssueProperty在保护句柄内确认内容、身份、InSync和Pin保持后产生元数据证明。比较允许沿同路径的完整戳链更新，不用宽泛时间／大小相等代替证明；有界512条，缺边退回重新核对。上传只忽略Attention展示字段，观察戳／快照戳须有证明，其他状态、代次、快照、回执及事件保持严格。

最小x64 Debug非打包编译通过（00940-minimal-build.log）；冲突及问题属性针对性39/39通过（00940-conflict/00940-conflict.trx）。包括单次选择等待其他文件、等待中取消、本程序属性发布不要求再选、外部同长度修改且恢复时间仍拒绝，以及既有本地／远端取舍、回执／版本变化与恢复备份保护。

## 最终验证

- 第一批针对性39/39后补充属性变化后的本地取舍路径；最终00940-debug-regression、00940-release-regression各1303/1303，失败／跳过均0。覆盖相关上传、改名、问题属性、冲突／恢复、取消及生命周期；明确排除用户停止的FailedMountTests、OrphanedMountRecoveryTests、ClearRemovalTests。
- 00940-delivery-debug、00940-delivery-unpackaged、00940-delivery-x64、00940-delivery-arm64均通过；x64／ARM64包签名及实际内容核验通过，正式Shell配置保持Menus，缩略图候选仍门控。
- 00940-arm64-tests-build交叉编译通过，未在ARM64执行；00940-compile-items.json为主232 Compile／11 Page、测试323 Compile，新增共享元数据证明类在两工程各登记一次，零重复。
- GPT 6.1 Sol只读复核状态广播、锁顺序、取消及元数据授权，未发现阻止提交的问题；没有执行测试，结论仅作源码审查。
- 使用上一版本完成收尾后的真实HKCU两视图基线。626个新增32位CLSID全部指向已删除的GUID测试目录，图标来源为本仓库测试输出，原生根及NameSpace引用为0。完整备份、删除前重验及独立后验见00940-regression-clsid-backup.json、00940-regression-clsid-cleanup.json、00940-regression-clsid-postcheck.json；基线旧项不清理，不宣称全机无历史残留。

## 交付与待测

签名包位于artifacts/packages/x64/HybridMount_0.0.9.40_x64_Test/HybridMount_0.0.9.40_x64.msix及artifacts/packages/ARM64/HybridMount_0.0.9.40_arm64_Test/HybridMount_0.0.9.40_arm64.msix；SHA256保存在validation/package-<架构>.sha256.txt。工具未安装最终正式包。

正式生产原始冲突场景、任务页GUI等待／取消与重复点击、Explorer目视、MSIX安装升级、最终非打包WinUI启动及ARM64实机均待测。自动验证仅回环服务和隔离NTFS／CFAPI根，没有操作正式挂载或真实远端、没有重启Explorer。缺少元数据证明边或真正内容／版本变化时仍要求重新核对，这是保留的安全边界。

本版独立本地提交、不推送；随后按用户顺序开始0.0.9.41排除与原生挂起状态，完成提交后再实施0.0.9.42日志回顾。
