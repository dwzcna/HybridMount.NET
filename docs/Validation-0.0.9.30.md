# 0.0.9.30：原生固定意图、及时释放与精确遗留清理

日期：2026-11-07。开发和自动验证预算2–4小时，Explorer目视、安装及外部等待另计。保留三个工程、SDK、IPC v2、数据库及配置／备份格式，独立本地提交，不推送。按用户要求先由GPT 6.1 SOL子代理只读分析，生产补丁由主执行者完成；另一个获授权子代理只负责D:\alidav的精确本地清空注销。

## 问题与实现

Windows原生“始终保留在此设备上”设置PINNED意图，不经过HybridMount菜单IPC。0.0.9.29没有消费该意图的后台下载入口；原生释放意图只由30秒缓存维护消费。双击打开会产生FETCH_DATA，因此能暂时绕开固定不下载的问题。使用保存的0.0.9.29程序集和新隔离夹具，属性句柄只设置PINNED后，文件仍不完整、1.5秒内远端新增请求0，证明不是普通正文打开的测试副作用。

- 每个连接增加独立Attributes监控，事件只合并维护唤醒，不进入上传队列；连接时恢复已有意图，保留30秒丢事件兜底。PINNED且内容不完整的干净文件每根单飞，全局最多两个后台下载；目录递归固定及零字节均处理。属性扫描不读取正文、不修改Pin或InSync。
- 原生固定与本程序显式keep共享会话内水合，工作自己拥有保护句柄和取消令牌；显式固定先用仅属性句柄核验身份并提交意图，下载在根锁外。上传未完成、远端待处理／删除、本地修改、身份变化及普通文件继续受保护。完成后重验身份、内容长度／时间、固定和同步状态，句柄及根锁归还后再通知Shell，不强设同步完成。
- UNPINNED复用安全脱水流程，立即由属性事件及水合资源释放事件唤醒，即使缓存软限额关闭也处理。共享等待者取消可能先于实际资源收尾，因此新增独立PinWorkCompleted通知，真正归还保护句柄及entry gate后再次维护，避免遗漏至30秒兜底。
- 一位等待者取消不影响另一位；程序任务取消不撤销Windows的PINNED意图，用户选择释放空间会取消该后台意图。断开／退出先停监控并取消任务，再等待会话共享工作释放句柄，最后释放源。重复停止／释放防重，水合自事件只用经保护核验的可靠文件戳过滤，不能按路径或时间窗忽略编辑。
- 完整诊断增加挂载缩略图开关及已安装清单中实际自定义预览处理器状态。普通Release包继续不绑定实验处理器，实验包显式绑定；没有证据表明缩略图导致原生固定入口缺失，未拔除功能。

## 实际验证

| 项目 | 结果 | artifacts/validation证据 |
| --- | --- | --- |
| x64 Debug最小主工程 | 独立Unpackaged输出通过 | 00930-minimal.log |
| 原生意图最终回归 | 12/12，失败与跳过0 | 00930-native-final.log／trx |
| 固定／释放／预览针对性 | 48/48；在最终资源通知补丁前通过，最终全量包含该补丁及新增2项 | 00930-pin-preview-targeted.log／trx |
| x64 Debug全量 | 1150/1150，失败与跳过0 | 00930-debug-complete.log／trx |
| x64 Release全量 | 1150/1150，失败与跳过0 | 00930-release-complete.log／trx |
| x64／ARM64普通签名MSIX | 签名、版本、架构及内容检查通过 | 00930-release-complete.log、00930-arm64-complete.log |
| x64实验缩略图Release签名MSIX | 自定义CloudFiles ThumbnailProviderHandler绑定及签名检查通过，未安装 | 00930-preview-package.log |
| ARM64测试 | 仅交叉编译通过 | 00930-arm64-tests-complete.log |
| x64非打包Release | 构建通过，未正常启动最终输出 | 00930-unpackaged-complete.log |
| Compile登记 | 主195／测试264，135项挂载源码各一次、无重复遗漏 | 00930-compile-check.json |

新增12项测试直接通过仅0x180属性访问的原生句柄设置意图，不双击、不读正文、不提交应用下载任务来代替原生菜单。覆盖缩略图开／关、连接恢复、递归目录、零字节、根锁外下载、与显式keep共享同一GET、取消其中一位等待者、下载阻塞时取消固定或断开、索引保护、远端删除／待处理屏障。确定性交错测试先让水合完整但保护句柄停在收尾处，取消等待者后确认仍未脱水，再归还资源，验证立即完成脱水。已有ShellUnpin／状态矩阵刻意停止新自动消费者，单独验证其原显式维护入口；自动入口由新测试验证。身份、Pin、InSync、Modified、无额外PUT及无意外上传记录分别断言。

最初预览针对性运行缺少同版本主程序解码worker，出现文件缺失／等待超时；完成0.0.9.30最小主程序构建后重跑48项及最终全量通过，不将这些启动失败称为产品缩略图缺陷已修复。两项并发测试的状态设置／断言修正后已复测，不保留为通过前的结论。

## 缩略图现场状态

用户在非打包D:\wdavtest开启ThumbnailsEnabled，保存值确为true；初次核对没有当前用户已安装MSIX或自定义处理器注册。用户随后测试0.0.9.29普通Release，截图PNG仍为关联图标，并已退出。普通包清单没有自定义云缩略图CLSID，挂载开关不能补装处理器；本版生成单独实验Release包供用户继续验收。HEIC未列入现有支持格式，不能按扩展名假定可预览。远端PNG仍须强版本和严格206／范围校验，弱／缺失ETag和忽略Range会保持普通图标；完整本地媒体可直接解码。本版不为缩略图触发整文件兜底或水合冷占位。

## 精确清理：坚果云历史目录

用户指定`D:\wdavtest\我的坚果云\HybridMount-upload-check-5e4cc11f6ace46319761b7b9a193a6fe`，历史记录可追溯至0.0.4.3。完整列表仅含原探测文件`.hybridmount-probe-52d3c421864b41fa8f809d0888749ad1`，2字节[17,31]，SHA256为026776CD13E217AF8FB2C67033C4417BCCFA17C07BA47E748D47182CA5C917C9。没有DAV锁通告，但精确DELETE返回409 FileBeingLocked，要求先解锁。

只对已核对原文件取得本次短LOCK、核对XML／响应头令牌一致并UNLOCK，再读回确认内容，精确DELETE文件及确认空的目录，均204；两者PROPFIND最终404。本地仅清理同原生身份的空占位目录，没有递归扩大范围，正式根身份及定义保持。没有新建远端目录，也未访问已取消NAS。

清理辅助脚本一次数组表达式错误曾额外请求`/dav//` DELETE，服务返回404；已停止并修正为发送前精确路径白名单，原始证据保留，不影响产品源码。首次锁解析拒绝服务省略角括号的头值，后续按生产现有规则核对XML与头值、只使用本次返回令牌完成解锁。证据为00930-legacy-inspect.json、00930-legacy-delete-attempt.json、00930-legacy-delete-retry.json、00930-legacy-unlock-final.json及00930-legacy-local.json。

## 精确清理：卸载后的 D:\alidav

用户明确授权不用保留本地内容。子代理核对本地NTFS、当前SID、原生Product／Schema／Owner及MountId=a28475bc-3fd7-47da-8705-87e494040f04、根文件身份和9项云占位均在边界内，再通过隔离仓库的生产离线clearAndRemove清空9/9项，源创建次数0、不水合、不复制正式凭据、不访问远端。

WinRT注销返回0x80070490，而原生／Shell注册仍在。确认Disconnected及根空、身份相同后，受支持CfUnregisterSyncRoot返回S_OK。仅该ID的SyncRootManager键和只属其每根随机GUID的HKCU NameSpace／CLSID仍残留；各自导出备份，立即重验唯一当前SID路径、TargetFolderPath、AUMID和无共享引用后精确删除，不改系统共享处理器、ACL或其他GUID。普通Shell变更通知后，原生、WinRT、SyncRootManager及两项命名空间注册均不存在，空D:\alidav保留且文件身份不变；其他三个正式根及完整配置哈希保持。

这是针对用户明确指定旧根的一次性收尾，不是0.0.9.30已具备通用遗留恢复界面的证明。普通用户恢复入口安排0.0.9.31；子代理证据位于`artifacts/validation/00930-alidav-20261107-cleanup/final-report.json`，最终Outcome=Completed、RemainingItems为空，Explorer目视未独立执行。

## 产物

| 类型／架构 | 签名包 | SHA256 |
| --- | --- | --- |
| 普通／x64 | artifacts/packages/x64/HybridMount_0.0.9.30_x64_Test/HybridMount_0.0.9.30_x64.msix | EAC361EF12D278AAA6E934D86DEB0FF52AD26189B3B2B27A57AA1D0E4D68EE78 |
| 普通／ARM64 | artifacts/packages/ARM64/HybridMount_0.0.9.30_arm64_Test/HybridMount_0.0.9.30_arm64.msix | 068EF0B33746AAD9680C12C7A24DC6460888B29713696E64A83A8B7EB7876C9E |
| 实验缩略图／x64 | artifacts/packages-preview/x64/HybridMount_0.0.9.30_x64_Test/HybridMount_0.0.9.30_x64.msix | A08CD2DAB51B8D46A9FBB216F68E5A2C7B53BFDFAD25666AD5AFA585B62CB1F1 |

实验包与普通包使用相同产品身份和版本，不应以卸载方式来回切换两种包；Windows可能拒绝同版本不同内容覆盖。工具没有安装、卸载或启动正式应用，也没有强杀或重启Explorer。用户随后反馈打包0.0.9.30仍无缩略图，系统级固定／释放菜单和详细信息状态列缺失，自定义菜单仍在；这些现象纳入0.0.9.32定位，不能将本版原生意图隔离测试等同于正式Shell验收通过。后续0.0.9.31／0.0.9.32按新版本交付。

## 待测与后续

真实Explorer原生菜单和本程序菜单的目视状态、实验MSIX冷PNG及坚果云媒体预览、正式安装升级、最终非打包Release正常启动和ARM64实机仍待测。历史SHELL-01及既有审查待办保持，不以本次全量通过宣称均已修复。0.0.9.31将提供归属核验后的遗留挂载处理，0.0.9.32将按用户最新反馈重新分析打包／非打包Shell集成与缩略图可行方案；它们尚未算本版代码完成。机器可读摘要：artifacts/validation/00930-evidence.json。本版本地提交，不推送。
