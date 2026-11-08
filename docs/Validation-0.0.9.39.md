# HybridMount 0.0.9.39 验收记录

日期：2026-11-09。起点7461bff，开工时无暂存或未提交变更；用户随后暂存的解决方案及工程修改按要求一同提交。预计代码与自动验证8—14小时，桌面等待另计。仅使用回环服务和隔离NTFS根，不操作正式挂载或真实远端，不重启Explorer，不推送，不继续0.0.9.33已停止专项。

## 代码交付

- 原生动态菜单按完整水合／未完整水合分别显示释放／保留；排除文件两者隐藏，目录含混合状态保留两项。混合选择执行前重查归属和状态。普通文件不为显示转换占位身份。
- 独立STA Shell宿主复用现有EXE／DLL，保留旧缩略图ABI及IPC v2，正常运行模式以真实包身份判断；当前包清单分别决定菜单及缩略图能力。
- 每挂载一个GUID命名SQLite BLOB库。旧共享库以一致快照逐根迁移，校验所有者、根身份与内容作用域，成功后只退休对应根；独立库含其他挂载时拒绝配置或迁移。移除等待工作结束、设置迟到屏障，再校验唯一根、检查点和关闭连接，精确删除DB及同名SQLite日志；失败保留配置供重试。
- requestThumbnail可异步使用有界范围读取和隔离解码，旧requestLocalPreview继续仅本地。8 MiB／32次范围读取、256 KiB窗口、10秒／512 MiB解码Job、两并发／128待处理；不接受200整文件退化，不在Shell回调等待网络或水合。正式缩略图能力仍关闭，因此正式包不会后台发起这些读取。
- Debug／Release为解决方案仅有的配置，保留x64／ARM64和参数选择的两种部署。主csproj为44行，部署／清单／VS菜单约定集中在build/HybridMount.Packaging.targets。用户提供的EPaperKit.Ax.csproj.tmp已阅读并通过受检删除补丁移除，未引入跨仓库依赖。
- 三个修改过的构建脚本使用UTF-8 BOM，兼容VS调用的Windows PowerShell 5.1。严格根查询遇0x80070001提供目标卷及CldFlt实例核验方法；失败仍是失败，不当成无同步根，不自动变更驱动或提权。

## 真实MSIX对照

| 配置 | 结果与证据 |
| --- | --- |
| MenusAndThumbnails | 00939-msix-54fea26f/result.json：部署／实际包激活成功；显式COM返回HBITMAP且alpha=2，但联机自动图像工厂0x8004B200，四文件StorageProviderState为空，自有及系统菜单未出现。请求12→12，没有新增水合或远端读取。自动接管失败。 |
| Menus（正式选择） | 00939-msix-8aeb3d4d/result.json：实际MSIX激活，自有及系统菜单在包内／独立非打包检查均出现，UIStatus列可枚举，联机／本地／固定／释放后的ProviderState为1/2/3/1。DB物理FileId一致、包外缓存命中，请求及持久占位状态保持。自动化门槛通过，Explorer目视另列待测。 |

两个随机身份的临时包均已移除；根、DB、原生／NameSpace／CLSID注册经独立非打包进程核验为0，见00939-probe-outside-cleanup-final.json。初次包外核验脚本拼接路径错误，修正后发现两个32位CLSID残值；按临时包报告的精确路径／GUID核验并备份删除，最终32项检查均为0。原始错误证据保留，没有重建正式根。

**未完成**：Explorer自动选择云缩略图处理器。正式源清单／产物采用空CloudFiles加独立fileExplorerContextMenus；构建拒绝组合候选及旧参数绕过。图片缓存、生成器和显式COM成功不能替代这一项验收。

## 自动验证

所有证据保存在artifacts/validation/。

| 检查 | 实际结果 |
| --- | --- |
| 动态原生菜单／IPC | 00939-menu/00939-menu.trx，1/1。 |
| 缓存首轮 | 21/22；一项夹具缓存所有权断言修正后00939-cache-repair单项1/1。 |
| Shell／缩略图／IPC首轮 | 81/82；关闭门控提前解析非法路径修复后00939-gate-repair单项1/1。 |
| 独立库归属及迁移 | 00939-cache-final，11/11。 |
| x64 Debug广泛 | 首轮1292通过、1项职责结构失败；将包清单运行查询移出Models后，00939-debug-final为1293/1293。 |
| x64 Release广泛 | 00939-release-final，1293/1293。 |
| 最后追加的本地CFAPI诊断 | Debug 00939-platform-diagnostics为2/2；Release诊断／职责00939-platform-release为8/8。保留原错误码及其他失败语义。 |
| 最终构建 | 00939-delivery-debug、00939-delivery-unpackaged、00939-delivery-x64、00939-delivery-arm64均通过；双架构签名及包内容核验通过，正式唯一配置为Menus。 |
| ARM64测试交叉编译 | 00939-arm64-tests-delivery通过；未在ARM64执行测试。 |
| 编译登记 | 00939-compile-items.json：主231个Compile／11个Page，测试321个Compile；零重复，新增及移动的共享源码均登记一次。 |
| VS菜单及脚本 | 00939-ps51-manifest.log清单生成成功；最终Debug／Release均求值为MSIX、HasPackageAndPublishMenu=true及Msix能力，见00939-vs-package-capabilities-final.json。实际VS右键显示待目视确认。 |

广泛回归明确排除FailedMountTests、OrphanedMountRecoveryTests、ClearRemovalTests，未继续用户停止的0.0.9.33专项。真实MSIX首次资源引用错误、菜单探针因测试输出被清空而缺程序集、首轮结构／路径门控失败等原始记录保留，修正后的结果单独记录。

广泛回归前记录真实HKCU两视图基线；回归后新增866个32位CLSID均指向本轮已删除的GUID测试目录，图标来源为本仓库测试输出，无原生根或NameSpace引用。逐项保存完整注册快照、删除前重验后精确清理并读回不存在，见00939-regression-clsid-backup.json和00939-regression-clsid-cleanup.json。初次清理的外层脚本误读旧LASTEXITCODE而报告失败，实际866项已清除，独立后验再次核对。基线前未归属历史项未修改，不宣称全机无残留。

## 用户生产反馈

用户运行0.0.9.39，在Windows 11企业版LTSC 10.0.26100、D卷NTFS／Fixed／Healthy上创建挂载遇0x80070001。只读WebDAV测试成功、CldFlt服务RUNNING；fltmc实例仅有C卷。用户在管理员终端执行fltmc attach CldFlt D:后，实例出现D卷，并明确确认“已经挂载了”。这是卷未附加过滤器的实际反馈；新错误提示只完成自动测试，未在该生产机复验。

## 产物与待测

正式签名包：

- artifacts/packages/x64/HybridMount_0.0.9.39_x64_Test/HybridMount_0.0.9.39_x64.msix
- artifacts/packages/ARM64/HybridMount_0.0.9.39_arm64_Test/HybridMount_0.0.9.39_arm64.msix

SHA256由构建脚本保存到package-x64.sha256.txt和package-ARM64.sha256.txt。工具未安装最终正式包。Explorer默认列、图标、排除与混合选择的目视／交互、正式MSIX安装升级、VS右键菜单目视、非打包WinUI最终启动、真实媒体兼容及ARM64实机均待测。缩略图自动绑定属于已执行失败且未完成，不能仅归类为等待用户测试。

用户要求本版独立提交后开始0.0.9.40，分析文件冲突取舍需重复点击的链路；本版不提前混入该修改。
