# 认证说明与验证步骤

本文件是提交文稿和检查步骤，不是通过报告。首次正式提交针对完成 0.0.25.0／0.0.25.1／0.0.25.2 后的统一 1.0.0.0 候选。涉及联网写入的测试仅对获授权的隔离服务和测试目录执行。

## 给发布者的准备清单

- 为实际宣传的 WebDAV、S3、群晖功能提供审核可访问的测试服务与账号。使用公开可信 HTTPS，账号限定专用目录／桶前缀，不提供生产访问权限。
- 通过 Partner Center 的 Notes for certification 交付地址、必要账号及操作说明；不要将密码、密钥、SQS URL 等实际凭据材料提交到 Git。说明账号有效期、服务在线时间及重置办法。
- 若演示 SQS，使用专用队列，说明是否启用及权限；未准备该功能的实际测试环境时明确列待验收。
- 测试资料应包含小文本文件、完整可下载的图片、子目录及足以演示任务进度的文件，不使用真实个人资料。
- Store 包为 Full；测试机器不应依赖开发用 SDK、用户手动导入开发证书或预装 .NET 才能启动。单独记录标准用户和两种架构的实际结果。
- 为产品范围隔离、直接卸载和重装测试使用隔离系统。不要在生产机上卸载包、删除框架、重启 Explorer 或改动正式挂载来获得证据。

## Restricted capability explanation: runFullTrust

HybridMount.NET is a packaged Windows desktop application running at the current user's medium integrity level. It uses Windows Cloud Files APIs to expose user-selected remote storage through local folders and communicates with its bundled native worker over bounded, authenticated local channels. The package also registers an out-of-process COM server for its File Explorer menu and thumbnail integration. These desktop and packaged COM features require runFullTrust.

The app does not request administrator elevation or install a custom kernel driver. The user supplies the remote storage account and selects the local mount folder. Child processes are checked against the expected package identity, executable, user, and session. File Explorer thumbnail reads use local cached data and do not initiate network reads or hydrate online-only files.

The Store candidate must not declare unvirtualizedResources. Packaged thumbnail data is stored within the actual package's LocalState and is shared only through the app's controlled storage and native readers. Registration cleanup is restricted to entries demonstrably owned by this product. This statement must be checked against the final manifest and actual installed behavior before it is submitted.

## Notes for certification — English draft

HybridMount.NET is a free desktop client for user-provided WebDAV, S3-compatible, and Synology storage. It does not provide a hosted storage account, subscription, trial, or in-app purchase. Dedicated test service details are supplied separately in these certification notes by the publisher; no production account is required.

1. Install the candidate from the certification package and launch HybridMount.NET as a standard user. Verify the reported version and architecture against the submitted artifact.
2. Open Manage mounts, create a connection using the supplied test service, and select a new empty local NTFS folder. Connection testing is read-only. Invalid credentials should leave the entered form values available for correction. Successful authentication accepts the connection task and opens the task page.
3. Open the local folder in File Explorer. Inspect remote placeholders, open a small file, and compare its bytes with the test source. Keep another file offline, then release its cached space. Check task details and local/remote status.
4. Create and edit a disposable file within the authorized test root. Verify upload results through the server. Some operations can be blocked if the server lacks permissions or reliable conditional-write protection; inspect the reported reason rather than treating a capability declaration as successful verification.
5. Run Detect all capabilities only in the disposable test root. It may create, modify, and remove remote test resources. Cancel a run, check the cleanup result and logs, and record any specifically identified leftover resource. Never delete remote items solely because their names resemble the app's temporary names.
6. Hide the main window and restore it from the tray. Confirm that closing to the tray differs from exiting. Verify that startup at sign-in is opt-in. Exit and restart while observing the current task's documented recovery state; cancellation does not imply that an already-sent remote write was rolled back.
7. Export a configuration using a test-only backup password and import it to the selection/form stage. Import does not automatically take over the previous root or restore task history. A backup without a password contains plaintext credentials; do not share it publicly.
8. Remove a disposable mount with Preserve cached files. Confirm that complete local files remain ordinary files, uncached content is not downloaded by this operation, the root folder remains, and remote content remains unchanged. Use a separate test root to verify removal without keeping mount-point files; that option also removes local edits.
9. In a separate installation scenario, test uninstall directly from Windows, reinstall, and upgrade to a higher candidate version. Check process exit, COM and startup registration, package data, user files, and remaining placeholders. Uninstall is not assumed to run an application cleanup dialog. Record any required user action and confirm the published uninstall instructions match the result.

The app exposes a privacy policy from its settings. The publisher must supply its publicly accessible privacy URL and actual support contact before certification. Core functionality requires access to the configured remote service, while existing complete local files and applicable local cleanup actions remain subject to their normal offline behavior.

## 实施验收记录要求

| 场景 | 必须记录的证据 |
| --- | --- |
| 包身份及进程 | 源清单与实际包身份、主／子／COM／预览进程的身份结果、失败阶段及回收结果 |
| 受限能力 | 最终包仅声明必要能力；缓存实际位置及普通用户读写结果；无权限放宽 |
| 文件保护 | 仅联机／部分／完整缓存、本地修改、未知上传结果、取消及重启后的内容与任务状态 |
| 缓存 | 主程序写入与原生读取一致；Explorer 取图不新增网络请求或水合；移除时只清本挂载缓存 |
| 产品范围隔离 | 不枚举旧包或因其存在阻止启动；实例、IPC2、配置、缓存和同步根仍属于当前产品及模式，不接管其他范围 |
| 新基线 | schema 9 新库正常；旧库、较新库、损坏及拒绝访问区分；失败前后原库内容保持 |
| 直接卸载 | 用户文件、占位、CFAPI、COM、启动项、缓存及后台进程的实际状态，不以“包已移除”代替逐项核验 |
| 发布渠道 | Full／Small 外部分发回归；Store Full 双架构统一版本、资源、依赖及上传容器检查 |
| 系统认证 | WACK 原始结果、失败分析和对应修正；Store 重签及实际获取另行记录 |
| 设备与界面 | 干净系统、21H2、ARM64、Explorer、DPI、高对比度、三语言及键盘；交叉编译不等于设备通过 |

支持账号和服务尚未提供时，相关行保持待验收；本地 HTTP 夹具、包签名或构建测试不能替代真实审核环境。通过审核也不自动触发公开发布。
