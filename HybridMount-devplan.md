# HybridMount.NET 开发计划（0.0.1—0.0.4.1）

更新日期：2026-10-30。当前版本：0.0.4.1，代码已实现；S3 真实认证与部分实机验收待完成。实现状态与测试状态独立维护；下列已完成／未完成仅描述代码。

工程限定为 HybridMount、HybridMount.Ext、HybridMount.Tests，主程序 HybridMount.NET.exe、命名空间 HybridMount。使用 WinUI 3／.NET 10／MSIX，最低 Windows 11 21H2，支持 x64／ARM64。本地挂载根仅限 NTFS。用户配置根路径为 `HKCU\Software\HybridMount.NET`，日志和运行索引使用 SQLite。

0.0.1 从 EPaperKit.Ax、ProKit.Ext 迁移必要代码，之后独立演进。协议驱动实现真实读写，自动双向同步另行规划。参考仓库只读，不增加跨仓库引用或额外产品工程。

下表为基础版本 0.0.1—0.0.4 仅人工、单人每天 8 小时的估算，不代表代理运行时间。本轮 0.0.3—0.0.4 开发/验证初始预计 90–150 分钟，实际运行与证据单独记录，外部服务及实机等待另计：

| 阶段 | 预计工时 |
| --- | --- |
| 0.0.1：迁移、主题、日志、托盘、启动、菜单和打包 | 12–24 小时 |
| 0.0.2：统一接口、模拟源、CFAPI 和保护 | 40–64 小时 |
| 0.0.3：WebDAV、状态和缓存 | 48–72 小时 |
| 0.0.4：S3 | 48–80 小时 |
| 跨协议回归和风险余量 | 32–56 小时 |
| 合计 | 180–296 小时，约 23–37 个工作日 |

本次 0.0.4.1 维护预算为开发及验证 60–100 分钟，外部认证等待另计；本轮开发与必要验证约 52 分钟（2026-10-30 01:51—02:43 UTC），提交与推送另计。后续固定测试按钮的界面修正预算为 10–20 分钟，沿用 0.0.4.1。

SMB／NFS 不列入版本和排期，仅在统一源契约、能力表及协议工厂中保留扩展接口。WebDAV 与 S3 本轮一并实施，自动双向同步另行交付；真实服务与实机等待另计。

## 已完成

### 0.0.1：界面、配置、日志、托盘与占位菜单（代码已实现，验收中）

- 迁移 WinUI 标题栏、左侧导航、公共样式、首页、日志页、设置页。移除原项目蓝牙、设备、日历、天气、行情和后台绑定。
- 迁移系统／浅色／深色主题和 None、Mica、Mica Alt、Acrylic、Thin Acrylic 背景；材质失败及高对比度回退，重启恢复配置。
- 配置保存在 HKCU\Software\HybridMount.NET，非法字段采用默认值，写失败保留原有效设置，不操作参考工程设置。
- 迁移 SQLite 日志队列，支持查看、筛选、复制、导出、清除，退出完成已接纳日志。
- 常驻通知区域，双击恢复，右键显示主窗口／退出，Explorer 重启后重新注册；失败时显示主窗口。
- 默认点击关闭隐藏到托盘，可设置直接退出；最小化维持任务栏行为；重复手动启动恢复已有窗口，退出按顺序释放 IPC、日志和图标。
- 登录启动默认关闭。MSIX 使用 StartupTask，非打包运行使用当前用户 Run 注册；启用后静默进入托盘，界面显示系统实际状态。
- 原生扩展在文件、目录、目录空白处显示 HybridMount 菜单及“释放空间”“始终保留在设备上”占位命令，通过 NamedPipe 记录请求并明确提示未实现，不修改目标。
- 提供 x64／ARM64 原生 DLL 和 MSIX。首版不提供存储协议，不注册 CFAPI 同步根。

- 全部应用／托盘／MSIX 图形替换为本项目骡子主题，保留 SVG 母版与 PNG／ICO 重新导出脚本。
- HybridMount.slnx 仅包含三个工程；提供 Packaged／Unpackaged 模式、MsixPackage／Project 启动项和 DebugUnpackaged／ReleaseUnpackaged 配置，输出相互隔离。

### 0.0.2：统一接口、模拟源与真实 CFAPI（代码已实现，验收中）

- 在主工程内部定义列目录、元数据、按范围读取、上传、建目录、移动、删除和取消接口；模拟源验证全部读写与错误行为。
- 能力表至少包含 RangeRead、ConditionalWrite、AtomicReplace、ChangeNotify、StableIds、CaseSensitivity，按连接与操作实测声明未知／支持／不支持。
- 元数据使用不透明版本令牌；ETag 不等于稳定 ID，原子替换不等于原子移动；不支持请求的保证时拒绝，写入取消或超时不冒充回滚。
- 内置确定的测试文件树，覆盖空文件、大文件、中文、多层目录，真实执行 CFAPI 占位、水合、固定和脱水，全程不联网。
- 验证本地 NTFS、空目录、权限、根重叠和其他提供程序归属；注册服务统一协调 WinRT 注册与 CFAPI 连接，不重复注册。
- 同进程托盘和 --background 模式托管 CFAPI，主窗口按需创建，关闭界面后继续工作。常规退出仅断开，保留同步根注册。
- 菜单仅在本产品挂载根内显示，支持递归、多选和逐项结果；普通目录与混合选择隐藏。
- 保护本地修改和新建内容，保留重命名身份和本地删除状态，重启核对实际状态，释放前排除修改和活动文件。
- 完成手动缓存控制及模拟挂载安全清理入口。

- 挂载导航固定在首页与日志之间，使用 MapDrive；移除前完整下载，本地编辑仅保护、不自动写回。

### 0.0.2.1：任务页、新增对话框与清空移除（代码已实现，验收中）

- 原 0.0.2.1—0.0.2.3 合并发布为 0.0.2.1；主程序、测试、原生扩展和 MSIX 统一四段版本。
- 导航为首页 → 挂载 → 任务 → 日志；任务 E7C4、日志 F120，挂载 MapDrive。
- 任务列表、进度、取消与结果迁到独立页面，任务仍由应用托管，页面选择恢复与订阅释放已实现。
- 新增挂载使用按钮 → MenuFlyout → 模拟挂载 → ContentDialog，保留名称、目录选择和自动连接；防重复提交并区分校验失败与连接失败。
- 移除对话框提供下载并移除、清空并移除、取消；默认取消。
- 清空包含修改和新增文件，保留空根目录，不水合或删除源内容；逐层锁定路径，链接只删自身。
- 移除模式／阶段持久化，支持中断后显式重试，禁止切换模式；清理成功后删除定义及条目索引，保留任务历史。

### 0.0.3：WebDAV、三态与自动回收（代码已实现，验收中）

- HttpClient + DAV XML，实现 PROPFIND／GET／PUT／MKCOL／MOVE／DELETE，不使用 WebClient 服务或盘符映射。
- Depth 1 枚举并控制并发，不假设标准 WebDAV 存在通用分页或增量游标。
- Range 验证状态、Content-Range 和版本；忽略 Range 时明确退化为整文件临时缓存，不宣称原生随机读取。
- 强 ETag 用于经验证的条件更新；弱 ETag、大小和时间仅作变化线索。LOCK、断点上传、条件写和原子替换独立探测。
- 默认 HTTPS，凭据不进 URL、日志或明文配置，XML 禁用外部实体。
- 生产程序移除模拟协议，测试保留模拟源；升级发现旧挂载时保留数据，提示迁出／清理，不自动绑定 WebDAV。
- 系统状态：本地＝已完整下载且固定（实心绿勾）；离线／仅远端＝未下载未固定（云）；待脱水＝已下载未固定可回收（空心绿勾）。保留原图标、缩略图和名称，不自制三套覆盖图标处理器。
- 固定中、下载中、部分下载、释放中及失败独立显示；待脱水不表示已经提交释放任务。
- 默认缓存软上限 10 GiB，可调或关闭；按需读取优先，实际占用含临时缓存。仅回收合格的未固定内容，修改、冲突和活动项保护。

- 首版远端非空目录删除拒绝；远端删除不自动传播，本地固定或修改与远端变化冲突时明确保留，等待用户处理。

### 0.0.4：S3（代码已实现，验收中）

- 使用 AWS SDK for .NET 的 AWSSDK.S3，兼容目标为 AWS S3 通用桶与 MinIO，真实服务验收仍待执行。
- 配置 endpoint、region、bucket、prefix 和寻址方式；支持 SDK 凭据来源、受保护的访问密钥及临时令牌，默认 HTTPS。
- ListObjectsV2 + prefix／delimiter／CommonPrefixes／continuation token 分页；分页 token 不作为增量游标。
- 前缀映射目录，零字节标记表达空目录；处理 Windows 名称、大小写及对象 a 与前缀 a/ 冲突，禁止静默覆盖。
- 按 VersionId 或验证过的 ETag 条件读取，校验单范围响应，ETag 不视为 MD5。
- 普通与分片上传，清理本程序的未完成上传；条件写和原子内容替换按实际端点验证。
- 移动采用复制后删除，明确非原子；目录递归移动记录逐项／部分完成结果，复制失败或条件不满足时保留源。
- 版本化桶区分删除标记与永久版本删除，默认不清空历史；取消／超时后核对状态，不盲目重复删除。
- 首版不要求云端事件基础设施，保留重扫；其他兼容服务按实测能力声明。

- S3 的复制与移动非原子；不支持目标复制条件的服务使用下载后条件上传。首次分片单独验证完成条件，只清理已知的本程序 UploadId，不全桶扫描清理。

### 0.0.4.1：协议兼容与失败挂载修复（代码已实现，部分服务与实机验收中）

- DAV XML 与 HTTP ETag 统一去除外围空白、识别强／弱／缺失／非标准标签。目录可选 ETag 不再阻断连接；裸值不自动成为强标签，也不用于条件覆盖。
- 缺少可靠标签的文件使用不可变整文件快照，校验大小、可用时间线索及下载后的元数据；能力降级。有效强标签的响应变化或版本冲突继续拒绝读取。协议版本默认严格相等，WebDAV 仅归一化等价格式，兼容旧索引令牌。
- S3 结构化诊断包含失败阶段、HTTP 状态、服务错误码、请求 ID 和区域提示；区分认证、签名、权限、桶／资源不存在、网络和证书失败。us-east-1 保持未验证默认值，不猜测区域。
- 新增“测试连接”，按表单当前值只读探测，三十秒超时、取消和防重复提交；不保存配置／凭据、不注册根、不恢复分片或探测写入。拒绝 HTTP 200 HTML 等伪成功响应。
- WebDAV／S3 新增和编辑对话框共用底部固定次按钮；测试／取消测试／取消中状态切换，关闭取消请求，创建及目录选择期间禁用测试。结果移出滚动表单，内容区上限 640、结果区上限 160，长诊断可滚动。
- 已知分片恢复与 ProbeAsync 分离；恢复失败提示告警，读取连接继续。诊断只公开经过筛选的字段，不保存原始响应、授权头或密钥。
- 注册阶段持久化为未知／未注册／注册中／已注册；旧配置结合原生／WinRT 注册、根身份、云占位和物化索引分类，查询错误按失败处理。
- 从未注册条目允许修正远端配置及凭据，非法输入保留原有效值；已注册根限制远端根变更。连接失败保留已保存配置，支持重试。
- 未注册挂载可离线保留文件移除，或确认后本地清空；根身份变化、其他提供程序和注册丢失伴随云占位时拒绝操作。普通链接只删除链接本身，保留空根目录和远端数据。
- 移除取消在途连接和任务，再按根串行执行；迟到连接不能重建已移除的根，网络探测后和注册前重新核对身份。模式固定、取消后显式重试和重启不自动删除保持有效。
- 完成后清理配置、DPAPI 凭据及条目索引，任务历史保留。主程序、扩展、测试和 MSIX 统一四段版本 0.0.4.1，IPC 继续 v2，仍仅三个工程。

## 未完成

- 本轮 0.0.4.1 范围内无待实现代码。真实 S3 认证与实机未通过项目按测试状态记录。自动双向同步、自动写回、冲突解决及后台周期重扫属于后续开发，暂不另定版本。

## 待测试

| 版本／类别 | 验收场景 |
| --- | --- |
| 0.0.1 配置与外观 | 重启恢复、非法值、保存失败、系统主题跟随、材质回退 |
| 0.0.1 日志 | 持久化、筛选、导出、清除、退出写完 |
| 0.0.1 托盘 | 关闭隐藏、托盘双击／菜单恢复、Explorer 重启、真正退出和注册失败 |
| 0.0.1 启动 | 实际登录启动、开关与外部禁用／系统策略；静默命令行与重复实例已验证 |
| 0.0.1 扩展／IPC | Explorer 中实际显示、目录背景、多选、扩展自动拉起程序、超时与并发压力；畸形帧及不改文件已自动验证 |
| 0.0.1 发布 | 最终签名包安装／升级／卸载、完整界面验收、ARM64 实机运行；双架构构建和包内容签名已验证 |
| 0.0.2 | 完整 GUI／Explorer、打包身份挂载与安装升级、并发／压力／异常注册恢复、移除中断故障注入及 ARM64 实机 |
| 0.0.2.1 | 完整任务／创建／移除对话框视觉及交互、真实 Explorer、打包安装升级、ARM64 实机；并发变化及 ACL 压力 |
| 0.0.3 | 其他 WebDAV 服务及真实写入兼容、GUI 三态/回收、并发与大目录压力；坚果云只读闭环在 0.0.4.1 已验证 |
| 0.0.4 | 真实 AWS/MinIO/CSTCloud（含实际 region/权限）、大对象与网络故障压力、完整 GUI/Explorer、MSIX 安装升级、ARM64 实机 |
| 0.0.4.1 | CSTCloud 现有凭据仍返回 401 / Unauthorized，未获得请求 ID 或区域提示，认证闭环未通过；待确认服务端签名上下文与授权 |
| 0.0.4.1 | 固定按钮在新增／编辑及长表单中的可见性、取消恢复和结果区域的 GUI 验收；编辑限制、移除二次确认、Explorer、安装升级和 ARM64 实机 |
| 0.0.4.1 | 注册丢失且实际残留云占位的系统故障注入、更多 ACL／并发压力；本机主动注销后系统解除云标记，未复现残留场景 |

## 已测试

2026-10-29，0.0.1：

- x64 Debug／Release：11 项自动化测试通过；含配置持久化／非法类型／失败保留、真实 SQLite 队列／清除／退出排空、帧边界、异常客户端恢复、单实例租约、真实原生 DLL COM 枚举和 NamedPipe 往返。证据：artifacts/validation/contracts-x64.trx、contracts-x64-Release.trx。
- x64：HybridMount.slnx 的 Debug（Packaged）与 DebugUnpackaged 构建通过；SDK 的 VerifyLaunchSettings 校验通过。证据：solution-x64-Debug-Packaged.log、solution-x64-DebugUnpackaged.log、vs-launch-profiles.log。
- x64：用户确认非打包模式可以运行。随后实际进程验证静默启动、静默重复启动不抢焦点、正常重复启动恢复窗口，以及两个占位操作不改文件均通过。证据：launch-x64-Unpackaged.txt。
- x64／ARM64：Release 自包含主程序、原生扩展和 MSIX 构建通过，签名验证及包内 PE 架构／运行时／新资源／菜单与启动声明检查通过。证据：managed-<架构>-Release-Packaged.log、signature-<架构>.log、package-<架构>.sha256.txt。
- 图标：本项目 SVG 导出全新 PNG／ICO，已检查 256px 骡子预览；包内旧图形／旧组件名称检查通过。

测试范围和待验收细项见 docs/Validation-0.0.1.md。桌面自动化工具因启动内核失败未能执行完整 UI／Explorer 验收，ARM64 没有实机结果；不记为通过。

2026-10-29，0.0.2：最终 x64 Release 自动化 19/19 通过，覆盖源契约、ABI、原生通信及真实 CFAPI 注册／占位／范围水合／固定／脱水／取消／修改保护／重连／保留数据移除，证据见 artifacts/validation/002-release-final-x64.trx。x64 非打包实际进程的静默启动、重复实例恢复、非挂载目录拒绝操作通过；x64／ARM64 Release 自包含 MSIX 构建、签名、CloudFiles 扩展与包内容检查通过。完整 UI、Explorer、打包安装升级和 ARM64 实机仍待验收，详见 docs/Validation-0.0.2.md。

2026-10-29，0.0.2.1：

- x64 Debug／Release 全量自动化各 25/25 通过，证据：0021-all-x64-Debug.trx、0021-all-x64-Release.trx。
- 新增 6 项覆盖旧配置默认保留模式、源内容不可用时清空未下载占位、修改／新增删除与外部链接保护、占用失败后重启重试、取消后恢复、根身份拒绝以及注销阶段恢复。
- x64 非打包静默启动、静默重复启动、普通重复启动恢复窗口及非挂载请求保护通过。x64／ARM64 自包含 MSIX、四段版本、签名、PE 架构及包内容检查通过。
- 界面自动化初始化两次均因 trusted Node process exited unexpectedly 失败，未记录 GUI／Explorer 通过。完整待验收项及产物见 docs/Validation-0.0.2.1.md。

2026-10-30，0.0.3：x64 Debug/Release 自动化各 33/33 通过，含本地真实 HTTP 和真实 CFAPI 网络挂载、刷新、缓存与保护。证据：003-all-debug.trx、003-all-release.trx。x64 非打包编译通过；真实坚果云账号、GUI/Explorer、打包安装升级和 ARM64 实机尚未验证。详见 docs/Validation-0.0.3.md。

2026-10-30，0.0.4：x64 Debug/Release 自动化各 46/46 通过，包括真实 AWS SDK HTTP 签名/分页/版本/条件/分片与取消恢复、部分移动、真实 S3 CFAPI 水合和保留数据移除、WebDAV/既有原生回归，以及生产旧模拟源只读迁出。x64/ARM64 签名 MSIX 0.0.4.0、AWS DLL/许可证与包内容验证通过。非打包实际进程的静默/重复启动、窗口恢复与非挂载目标保护通过。无凭据服务探测均返回 401；桌面工具初始化及重试失败，未记 GUI 通过。证据与边界见 docs/Validation-0.0.4.md。

2026-10-30，0.0.4.1：

- 最终 x64 Debug／Release 各 80/80 自动化通过。新增覆盖 ETag 各形态／格式兼容／真实冲突、S3 401／403／签名／区域／非标准响应、只读探测与分片恢复分离、超时／取消、TLS、注册前失败移除、旧配置迁移、物化索引恢复、连接／移除竞争、根替换和外部链接保护。证据：artifacts/validation/0041-all-debug.trx、0041-all-release.trx。
- 坚果云使用本机保存的凭据只读认证成功；独立 NTFS 根中完成真实 CFAPI 占位创建和 176,202 字节读取，内容与直接读取快照一致，观察到整文件缓存降级。验证根及缓存已清理。证据：0041-real-webdav.log、0041-real-services.json。
- CSTCloud 按原配置 HTTPS／Path-Style／SigV4／us-east-1 复测仍为 401 / Unauthorized，无请求 ID 或区域提示。仅确认诊断可用，S3 真实认证与读取未记通过；未尝试其他区域。证据：0041-real-services.json。
- x64 非打包进程的静默／重复启动、窗口恢复及非挂载请求保护通过；实际启动后 WebDAV 为本产品已注册根，失败 S3 为未注册。证据：0041-launch-x64-Unpackaged.txt、0041-live-mount-state.json。
- x64／ARM64 Release 自包含签名 MSIX、四段 0.0.4.1、PE 架构、CloudFiles／菜单／启动声明、AWS DLL／许可证及二进制版本核对通过。证据：0041-release-*-build.log、0041-binary-versions.json。
- 桌面工具初始化失败，重置后重试仍失败；未执行 GUI／Explorer 验收，也无 ARM64 实机结果。详细记录与产物见 docs/Validation-0.0.4.1.md。

2026-10-30，0.0.4.1 固定测试按钮维护批：x64 Debug／Unpackaged WinUI 与原生扩展编译通过，现有 ConnectionTests 12/12 通过（真实 HTTP／SDK、超时／取消及只读探测回归）。证据：artifacts/validation/0041-dialog-fixed-build-x64-Debug.log、0041-dialog-fixed-connections.trx。桌面工具初始化与重置重试失败，未执行 GUI 点击／布局验收，见 0041-dialog-fixed-gui-attempt.txt。本批只验证 Debug 输出，初始 Release／双架构签名包的历史结果见上文。

## 核心参考资料

- [Cloud Files 架构与 CloudMirror](https://learn.microsoft.com/en-us/windows/win32/cfapi/build-a-cloud-file-sync-engine)
- [CfRegisterSyncRoot](https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfregistersyncroot)
- [CloudMirror 注册示例](https://github.com/microsoft/Windows-classic-samples/blob/main/Samples/CloudMirror/CloudMirror/CloudProviderRegistrar.cpp)
- [MSIX desktop3:CloudFiles](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-desktop3-cloudfiles)
- [AWS SDK 凭据](https://docs.aws.amazon.com/sdk-for-net/v4/developer-guide/creds-assign.html)
- [S3 枚举](https://docs.aws.amazon.com/AmazonS3/latest/API/API_ListObjectsV2.html)／[读取](https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetObject.html)／[上传](https://docs.aws.amazon.com/AmazonS3/latest/API/API_PutObject.html)／[复制](https://docs.aws.amazon.com/AmazonS3/latest/API/API_CopyObject.html)／[删除](https://docs.aws.amazon.com/AmazonS3/latest/API/API_DeleteObject.html)
