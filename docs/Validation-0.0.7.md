# HybridMount 0.0.7 验收记录

日期：2026-11-03（Asia/Shanghai），Windows x64，.NET SDK 10.0.401、Visual Studio 2026、Windows SDK 10.0.26100.0。产品 0.0.7，程序集、原生扩展、测试及 MSIX 0.0.7.0。开发、技术验证及自动化预算 6–10 小时，12:28 UTC 开始，真实服务及桌面／实机等待另计。保留三个工程、IPC v2、AWSSDK.S3 和缩略图正式注册门控。

## 已完成

- WebDAVClient 3.3.0 的 DavClient 全方法迁移，依赖固定、实际 NuGet MIT LICENSE／NOTICE 随包。应用拥有 HTTP 客户端和根限定认证处理器，禁重定向，XML 有界解析，保留强／弱／缺失／裸 ETag 及旧令牌兼容。
- 普通 Range 被忽略的 200 经分类、强版本校验后用库重新完整 GET 并共享快照；严格预览不退化。错误 206／版本冲突不回退；元数据与 REPORT 120 秒总截止，流消费 120 秒无进展截止，共享工作取消独立。
- 每目录 sync-level 1、初始／截断续批、无效令牌重建、明确不支持回退 Depth 1；仅资源级 404 删除、完整缺失再确认。SQLite v5 原子提交 shadow／事件／检查点，待应用阶段可重放，scope 隔离账号根与凭据修订。
- 每挂载一个循环、全局两个，间隔从上轮结束计算，故障退避、Retry-After、认证暂停。网络在根锁外，提交重验会话；断开／退出先发停止信号再等待，迟到连接不重启调度。弱版本观测代次隔离缓存；同内容回显只在原始 SHA-256 与新快照证明相等后迁移基线，不吸收本地新编辑的观察戳。
- 固定文件完整校验快照、持久 Committing／Hydrating、排他保护、原位 CfUpdatePlaceholder／固定恢复／快照水合，保留占位身份。不删除重建，不用普通覆盖代替 CFAPI；零字节使用透传文件系统元数据以实际截断。元数据更新时同步拒绝迟到旧水合。
- 挂载页远端检测开关、间隔及实际状态，任务显示阶段／字节／取消重试。备份写 v2 兼容 v1，导入不连接；运行游标、事件、快照不进入备份。
- F05 MOVE 207 子项失败、F06 更新锁可靠存在性条件及意外空资源精确回滚、F07 正文消费截止已纳入迁移及自动化。

## 未完成

本版范围代码已实现。S3 自动检测／SQS 属于 0.0.8，F04、F08–F11、LAN-01／02 和 SHELL-01 保留原后续边界。本轮改变了连接／断开协调，未把未专门验证的 F04 所有窗口记为整体修复。

## 待测试

- 真实支持 RFC 6578 的服务有效 REPORT、每目录／多层集合／截断续批及令牌失效；本轮坚果云明确不支持，模拟服务仅验证客户端语义。
- WinUI 检测设置、切换选择不写回、任务取消／重试等视觉交互，Explorer、安装升级、跨机器／跨版本数据库与备份迁移、ARM64 实机。
- 固定提交各操作系统／存储条件的崩溃和磁盘故障恢复、占用及与其他客户端的真实并发；本机原生技术测试不代替多平台认证。
- 历史 SHELL-01 的 11 项失败根因与确定性交错，本次通过不修改历史失败结论。
- 正常托盘退出及退出码 0 仍待实际 GUI；本轮非打包启动通过后，无在途上传时仅中断本轮测试进程，不能当作正常退出。
- NAS `10.0.0.20:5007` 测试按用户指示取消。此前探测未创建挂载或远端文件，不再请求此服务。
- Computer Use 两次初始化（含 reset）均返回 `windows sandbox failed: helper_unknown_error: setup refresh had errors`，未进行 GUI 输入，证据 007-gui-nas-status.json。

## 已测试

证据位于忽略目录 artifacts/validation。失败的诊断轮次保留，最终结果仅以修复后的输入为准。

| 范围 | 本轮实际结果与证据 |
| --- | --- |
| x64 完整自动化 | Debug／Release 各 309/309，0 失败、0 跳过，007-contracts-x64-Debug.trx／007-debug-final.log、007-contracts-x64-Release.trx／007-release-final.log |
| 驱动与同步 | 各阶段 45/45、37/37、74/74；最终同步／回显／P1 48/48：007-sync-echo-final.trx；XML／GET 停滞、取消、MOVE 207、锁条件忽略及异常创建回滚等包含在完整回归 |
| 真实 NTFS／CFAPI | 15/15：007-refresh-read-oplock.trx，固定 0／3／700000 字节及 4096 字节切换后中断重放，身份／固定／摘要；旧版水合竞态最终回调屏障也包含在 48 项及全量 |
| 真实坚果云 | 007-jianguoyun-sync-61b91879c9254bfd9f62c35cd11e2ae9/report.json：文件系统触发自动上传、独立 SHA-256、65573 字节固定自动更新、重启后零字节、远端删除不复活全部通过；服务不支持 RFC 6578，实际使用完整对账。只用既有“我的坚果云”内唯一文件，无远端测试目录；远端文件与独立同步根清理 Completed |
| 非打包实际启动 | 007-launch-final.log、007-launch-x64-Unpackaged.json／txt：Ready、静默无窗口、静默重复实例不激活、普通重复实例恢复主窗口、非挂载缓存请求拒绝。用户原实例已退出后才启动，精确管理本轮进程；受控中断不代表正常托盘退出。正式根前后 Owned=True，待上传／潜在本地改动均为 0，007-original-roots-prelaunch.json／after.json |
| 构建与包 | x64／ARM64 正式签名 0.0.7.0 MSIX 的签名／版本／资源／PE／自包含及许可证检查通过，007-package-x64-final.log、007-package-arm64-final.log；ARM64 测试工程交叉编译 0 警告／错误，007-tests-arm64-final.log，无 ARM64 运行 |

### 修复过程中保留的失败

- 007-weak-echo-core／007-retry-diagnostic／007-retry-counts：取消后重试遇到旧水合与更新竞争。补充安全的原生状态诊断、排他只读元数据句柄及原子回调屏障；007-sync-echo-final 同用例及新弱版本摘要基线回归通过。
- 新弱版本回显测试最初未提供写锁服务，正确拒绝覆盖；改用已有受控独占锁服务，完成基线迁移、编辑保留及实际上传验证。未放宽无锁条件下的写入保护。

### 行为与边界

RFC 6578 仅发现变化，不赋予弱 ETag 强条件语义。缺少可靠版本的完整对账仍依赖服务器提供的大小／修改时间等线索；明确弱变更有新观测代次，不能复用旧内容缓存。坚果云不满足安全覆盖的能力仍提示冲突／另存副本，自动检测不改变该保护。

干净固定文件远端更新在网络下载期间保留原内容；本地修改、占用、类型互换及已发送上传保持待处理／冲突，不能仅因令牌已前进丢失事件。取消本版本不立即重排，显式重试或下一版本恢复。

### 参考资料（不是测试证据）

- https://www.nuget.org/packages/WebDAVClient/3.3.0
- https://github.com/saguiitay/WebDAVClient/blob/1e90c8985244e744d5390ceb3959222f9e0f6eff/docs/collection-sync.md
- https://www.rfc-editor.org/rfc/rfc6578.html
- https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfupdateplaceholder
