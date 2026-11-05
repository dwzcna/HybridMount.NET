# HybridMount 0.0.9.3 验收记录

2026-11-05—06，Windows x64。开发与自动验证预算3–5小时，安装占用及桌面等待另计。0.0.9.2先行本地提交`18676c5`，本版随后独立实施；按授权仅本地提交，不推送。

## 代码完成

- 版本统一0.0.9.3；保持三个工程、SDK、IPC v2、SQLite v6及配置格式。非二进制修改全部先生成补丁，经`git apply --check`和`git apply`后应用。
- 基础CloudFiles状态、属性、横幅及菜单声明常驻；Windows SDK清单要求五个默认节点成组出现，保留无Clsid的默认ThumbnailProviderHandler，仅实验包为该节点设置自定义Clsid。包检查同时核对重复节点及实验门控。
- 上传完成的原生保护句柄释放、数据库事务提交、根锁释放后独立通知Shell；不依赖缩略图开关，通知失败不重传或撤销已经完成的上传。
- 完整诊断按需显示实际运行／安装版本、包身份、处理器、三个条目的上传／索引／CFAPI／只读Shell属性。State=10为待处理，Transfer=512为未完成，不能将512解释为暂停位8，也不以根IDLE代替文件状态；不新增常驻说明小字。
- 查明S3示例文件的原因：完整缓存仍为UNPINNED（请求释放内容），Shell因此保持待处理。30秒维护在软限额关闭或尚未超限时也完成既有释放请求；在根锁、排他保护引用内复核根、条目、对象身份、状态戳、同步与修改状态，并排除占用、固定、上传未决和远端屏障。真正脱水后才通知，不设置InSync或改写只读Shell属性，不主动水合。冷占位用纯属性句柄提前跳过，重复维护不清除UNPINNED或推进USN。
- DAV清理须取得强ETag并核对精确日志中的SHA-256；最多三次等待可靠元数据，五秒总截止覆盖正文核对与条件删除，关联调用取消。弱／裸／缺失验证器保留CleanupPending，内容／版本变化保留Foreign，不无条件DELETE、不重发已成功上传。新普通名称及旧隐藏名称精确日志继续兼容。

## 自动验证

| 验证 | 实际结果与证据（artifacts/validation） |
| --- | --- |
| DAV针对性 | 104/104；0093-dav-targeted.log／trx |
| Shell最终针对性 | 24/24，含16项安全释放保护和状态矩阵；0093-shell-final-idempotent.log／trx |
| x64 Debug完整 | 539/539，零失败、跳过；0093-Debug-final.log／trx |
| x64 Release完整 | 539/539，零失败、跳过；0093-Release-final.log／trx |
| WinUI非打包 | x64 Debug最小编译及Release编译通过；最终幂等版ReleaseUnpackaged独立配置编译通过，输出不覆盖运行文件；0093-unpin-minimal-debug.log、0093-Release-unpackaged-final.log、0093-ReleaseUnpackaged-final.log |
| 签名MSIX | 最终x64／ARM64自包含、版本、资源、原生扩展、处理器及签名验证通过；0093-package-x64-final.log、0093-package-ARM64-final.log |
| ARM64测试 | 最终仅交叉编译通过，无警告／错误；0093-tests-ARM64-final.log；未运行ARM64实机 |
| 编译登记 | 主工程172、测试211项Compile，无重复；0093-structure-packages-final.json |

安全释放回归覆盖缓存限额0／无压力、零字节、重复维护及状态戳稳定、普通缓存、固定、修改后还原普通文件、占用、Queued／Sending／OutcomeUnknown／Conflict／Failed／Canceled、远端待处理／删除、取消和重启。维护不产生远端写入，原有上传、远端删除及固定更新完整回归保持。

失败证据未覆盖：DAV修复前缺失／弱／裸验证器3/3失败；释放请求修复前3/3失败；重复维护前2/3失败。保护回归首轮55/57中，两项是测试错误地用SaveEntry代替独立远端存在状态入口、以及假设截断编辑后一定还是占位，按实际契约修正，保护复测17/17通过。早期Release全量521/522遇到已有GIF工作进程10秒截止，独立预览6/6及完整522/522复测通过；未放宽超时或声称历史Shell超时根因修复。最终两配置539/539均含全部新增用例。

## CFAPI语言与真实S3结果

官方Windows SDK C++只读工具直接调用CfGetPlaceholderInfo和Shell属性；与C#读值一致。SDK标准信息64字节，身份长度偏移56、身份内容60；回调152字节，操作及参数均48字节。未发现本问题由C# ABI引起，不迁移整个驱动或增加工程。诊断工具保留于忽略目录，证据为0093-cpp-sdk-state-final.txt／after.txt。

正式`D:\s3test`十个PNG升级连接后原来仍为InSync=1、Modified=0、Pin=2，Shell State=10；仅更新注册或发送通知不足以修复。程序完成原有释放请求后，十个文件全部State=1（仅联机），缓存字节为0，仍已同步。文件大小、文件ID、挂载／条目标记及上传记录保持，证据0093-s3-shell-final.json、0093-s3-native-final-identity.json、0093-s3-state-restored.json。该现场的首次维护版随后暴露冷占位第二轮打开可使系统将Pin归零，最终代码已用前述幂等回归修复；不把该现场记录中的AllUnpinIntentsRetained=false改写为通过。

按原程序模式正常打开一个代表文件`屏幕截图 2026-09-26 221423.png`，水合104733字节，SHA-256与持久上传基线一致（4CAF15439D074D36C68DF40FC358C0E7C1A1886DD011942554AB1CD4639353F6），Shell恢复State=2（已同步）；未执行远端写入。证据0093-s3-open-verified.json。真实Shell属性通过不等于已目视验收Explorer工具提示。

## WebDAV实服闭环及正式数据保全

仅使用现有`https://dav.dwzcna.site/`根内唯一文件`hybridmount-validation-0093-2193d00993dd4816841f4a88bf045b53.txt`，71字节，从隔离NTFS根经新驱动／文件系统事件自动上传Completed。GET摘要F8E2C97B55B38CDB8B322684D98BE7470000CB8F1A06F1E5A6C872BE04D0CBBC，强ETag匹配后If-Match DELETE为204，最终PROPFIND 404。临时日志清空，隔离同步根经公开流程注销；证据0093-myweb-filesystem-live.json的Passed、CleanupVerified、LocalUnregistered均为true。未创建远端目录，未访问已取消的NAS。

正式`D:\myweb`／`D:\s3test`定义摘要、根身份、上传状态／代次和旧五条隐藏临时记录逐项保持；三个SHX仍Failed，未自动重试，既有Canceled也未恢复。证据0093-formal-before-upgrade.json、0093-formal-preservation-final.json（三项均true）。本轮首次Shell测试收尾失败留下的隔离根已按精确ID及身份通过公开clearAndRemove注销，见0093-test-root-cleanup.json；不按名称批量删除。

## 安装、运行与未通过项

- 初次0.0.6.1→0.0.9.3原位升级成功，基础五处理器已注册；旧扩展宿主占用曾返回0x80073D02，使用正常延迟注册并等宿主自然退出，没有强杀或重启Explorer，见0093-upgrade*.json。
- 用户正常退出后，包含主要释放修复的Release非打包程序通过静默启动、重复实例退出、窗口恢复及非挂载操作拒绝；保留PID31884运行。前一会话ExitCompleted、无fatal，新进程实际版本0.0.9.3／Unpackaged，见0093-launch-final-unpackaged.txt、0093-final-lifecycle.json。
- 最终包更新失败：Windows事件404／474确认0x80073CFB，同版本同标识但内容不同。最终签名产物已生成，已安装包仍是本轮早期0.0.9.3载荷，不能将最终载荷记为已安装。未卸载正式包、改版号、复制配置或绕过系统部署，见0093-install-final-package.json。
- 用户已告知后续无法响应；桌面助手初始化失败，未用其他UI自动化绕过。当前进程包含主要修复，但没有最后的冷占位幂等优化；最终非打包产物位于`HybridMount/bin/x64/ReleaseUnpackaged/Unpackaged/net10.0-windows10.0.26100.0/win-x64/`，保留当前进程，不强杀以替换文件。该最终产物冷启动、最终MSIX安装、Explorer目视状态、界面对话框、ARM64实机继续待测。
- 正式三个Failed由用户在使用新版后显式重试；本轮唯一文件闭环不能冒充这三个历史任务已完成。历史SHELL-01、TEST-RUNNER-01及其他审查待办保持单列。

## 最终产物

- x64-normal：`artifacts\packages\x64\HybridMount_0.0.9.3_x64_Test\HybridMount_0.0.9.3_x64.msix`，SHA-256 `81e325c92ff3cc4c75da4a4b1d84bb877b7b216fb2568435bd3ba0c3d17cc3f0`。
- ARM64-normal：`artifacts\packages\ARM64\HybridMount_0.0.9.3_arm64_Test\HybridMount_0.0.9.3_arm64.msix`，SHA-256 `84aba63e3895c92b465afbe4ec66fa2ffaa11a7cbd939f14b5a641c9e1cc4210`。

本版已完成代码与上述自动验证，本地提交，不推送；外部部署限制及未执行项按上述边界保留。

参考（不计测试）：[CFAPI](https://learn.microsoft.com/zh-cn/windows/win32/api/cfapi/)、[CF_PIN_STATE](https://learn.microsoft.com/en-us/windows/win32/api/cfapi/ne-cfapi-cf_pin_state)、[CfSetPinState](https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfsetpinstate)。
