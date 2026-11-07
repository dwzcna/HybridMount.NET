# HybridMount 0.0.9.33 验收记录

日期：2026-11-07。版本、程序集及MSIX清单统一0.0.9.33。用户在首轮针对性验证后明确要求“停止0.0.9.33版本的测试，完成代码后结束并提交”。本版按代码交付；不再运行后续测试、编译、打包或安装，不推送。

## 代码完成

- 三种移除入口共用MountRemovalCompletion。独立Schema2 JSON日志兼容Schema1，保存根身份、移除方式、精确注册备份、内容完成证据、注销与Shell阶段、最近失败及HRESULT。保留SQLite和挂载配置格式。
- 内容处理完成后不重新清空／下载；原生不存在后不重连。WinRT优先注销，仅不可见或0x80070490时核验本产品当前SID、实际根FileId、内容结果后原生回退。新内容、身份变化、占用或无法查询时保留现场。应用记录在Shell核验后才清除；启动只恢复展示状态。
- 主程序提前分流维护模式，MSIX隐藏入口使用独立win32App／mediumIL，主App行为不变。私有有界管道校验双方PID、同映像、当前SID／会话及随机标识，不增加公共IPC操作，不接受任意注册路径。
- 包内和真实视图分别捕获精确快照及所有者，处理本产品每根注册、Namespace／CLSID和HideDesktopIcons对应命名值。删除前核验未变化及唯一引用，保留共享键、其他值和其他挂载；真实视图再次读回之后才完成。
- 遗留发现包含仅剩Shell入口和旧Completed日志残留；仅剩入口操作保留目录和文件，确认文字随类型变化。诊断显示实际阶段、失败HRESULT、注册残项及包外结果，移除期间缺失的旧索引文件显示“本地已删除”。

## 已执行证据

1. **隐藏入口真实HKCU隔离门槛通过**：独立临时包HybridMount.MaintenanceProbe.55036227，主入口使用Windows.FullTrustApplication，维护入口使用win32App／mediumIL，共用测试宿主。独立非打包PowerShell观察：写入前无GUID键；主入口写入后仍不可见；维护入口写入后真实视图可见；维护入口删除后真实视图不存在；卸载临时包后仍不存在。未使用正式同步根或远端服务。
   - `artifacts/validation/00933-maintenance-gate-55036227/result.json`
   - 同目录的编译、makeappx及签名日志。此项证明入口的注册表运行行为，不能替代最终产品私有维护协议端到端验收。
2. **阶段性最小x64 Debug非打包编译通过**：维护入口第一批和统一收尾批次编译完成；统一收尾首次发生字符转义错误，已修正。
   - `artifacts/validation/00933-batch1-build.log`
   - `artifacts/validation/00933-batch4-build.log`
   - 这些成功对应中间代码状态；后续界面、诊断和边界修正未重新编译，不能作为最终源码编译通过证据。
3. **首轮移除针对性回归23通过、1失败（共24，跳过0）**：ClearRemovalTests与OrphanedMountRecoveryTests。失败为ConfigurationAppearingBeforeUnregistrationStopsCommit，暴露阶段记录后至WinRT注销前缺少配置重验；最终补丁已补上紧邻注销的内容／归属核验，按用户指令不再复测。
   - `artifacts/validation/00933-first-removal.trx`
   - `artifacts/validation/00933-first-removal.log`
4. 用户停止指令后未启动测试、构建、打包或安装；提交前仅检查源码差异和补丁有效性。没有将历史1178项结果沿用为本版结果。

## 用户授权的现场清理

用户追加要求由子代理q清理Explorer残留。q独立核对包外HKCU，发现截图两项仍有实际原生空根，并非仅剩入口：D:\myweb、D:\wdavtest；当前SID／实际根FileId及产品标记匹配，目录为空，现场未见已安装包或运行主程序。基于本轮显式清理授权，限这两个精确空根执行备份、再次核验、注销及对应Shell收尾；出现内容、身份变化或查询失败即停止，不清空目录，不修改配置，不重启Explorer。

现场收尾已完成：

- MySvr：D:\myweb，根ID `7a6bcdf55b134c6eadb3bc7648403da8`，Namespace `{66B169AD-66AC-4701-99F6-14FD0D8043BB}`。
- WebDAV 挂载：D:\wdavtest，根ID `472eb61e060a4e009b8cf1107b90ab84`，Namespace `{8FF5535C-E7C4-4CE2-B061-31CA12DD62B7}`。
- 先保存7份精确注册导出备份；WinRT独立枚举只返回OneDrive，两根不可见。持有属性路径租约并再次核验空目录／精确身份后，原生注销各返回S_OK；随后删除对应2个HKLM SyncRootManager、2个HKCU Namespace、2个每根CLSID及2个HideDesktopIcons命名值。
- 新的独立非打包观察进程PID21856，GetCurrentPackageFullName=15700；两路径原生查询均为0x80070186，全部目标Shell注册不存在。根目录及FileId保持且仍为空；OneDrive、其他31个隐藏值摘要和Explorer PID24540保持。
- 本轮门槛测试GUID子键已不存在；当前SID拥有且完全为空的ShellMaintenanceProbe测试父键备份后删除。不删除Software\HybridMount.NET及其他配置。
- 文件／目录删除0，正式配置修改0，远端请求0；未停止程序或重启Explorer。Explorer目视未执行，不将包外注册核验当作目视结果。

证据目录：`artifacts/validation/00933-user-residual-cleanup/`，含`audit-before.json`、`backup-manifest.json`、`cleanup-report.json`、`final-verification.json`。现场操作结果与产品流程验证分开记录。

## 待测试及限制

- 最终源码最小编译、注销前配置变化修正复测，以及新增私有维护通道的真实产品端到端。
- 完整移除恢复矩阵：阶段取消、退出、重启、重复操作、部分注册失败、新内容及身份替换；旧日志、历史清理和无索引恢复。
- 注册边界矩阵：缺失／非空目录、共享CLSID、快照改变、权限不足、无关隐藏值、缺失目标类、其他挂载及用户记录。
- 独立临时包的最终产品流程与卸载重装；包外再次验证全部测试元数据清除。
- x64 Debug／Release全量、最终非打包启动、x64／ARM64签名MSIX核验。ARM64实机、WinUI／Explorer目视及正式安装升级均未执行。
- 本版不改缩略图绑定和水合策略，0.0.9.32记录中的历史Shell问题仍单列。

## 交付

受检补丁依次应用；只暂存本轮相关文件，独立本地提交，不推送。没有生成可宣称验收通过的0.0.9.33发布包。
