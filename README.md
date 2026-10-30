# HybridMount.NET

当前版本 **0.0.5.1**，面向 Windows 11 21H2 及以上，支持 x64／ARM64，使用 WinUI 3、.NET 10、MSIX 与 Windows Cloud Files API。

导航为“首页 → 挂载 → 任务 → 日志”，设置位于底部。挂载使用 MapDrive 图标；品牌资源采用银灰色硬盘与骡子探头图形，覆盖程序、托盘、扩展及 MSIX 全部缩放资源。

## 使用

1. 在挂载页点击“新增挂载 → WebDAV 或 S3 对象存储”，填写 HTTPS 服务器根、账号及应用密码，选择本地 NTFS 空目录，点击“创建并连接”。WebDAV 使用账号与应用密码；S3 填写 endpoint、bucket、prefix、region 和寻址方式，选择 SDK 默认凭据链或填写访问密钥/临时令牌。新增及编辑对话框底部固定显示“测试连接”，测试期间改为“取消测试”，结果在表单下方显示；测试不保存配置或注册根。us-east-1 是未验证默认区域，应按服务商说明确认。连接失败保留配置供重试；从未注册成功的挂载允许编辑服务器、桶和前缀，已注册挂载保持远端根限制。S3 错误会显示失败阶段、HTTP 状态、服务码、请求 ID 和服务端区域提示（如有）。
2. 在资源管理器浏览真实占位文件，打开时按需读取；“始终保留在设备上”固定并下载，“释放空间”解除固定并回收合格缓存。本地修改、新建、活动文件和状态不明项目受到保护。
3. 挂载页支持连接、断开、刷新、打开目录和扫描本地改动。连接期间持续上传本地新增和修改，自动上传开关已移除；升级前的旧改动也自动纳入。取消、冲突、结果不明和远端删除保护继续生效。独立任务页区分文件、目录及副本的远端目标，保留已完成上传的历史，显示字节进度、等待、冲突和结果待核对，支持取消、重试、另存远端副本及逐项结果；切换页面后任务继续。
4. 在远端删除文件或目录后，点击“刷新”或重连挂载。确认删除后会清理本地未修改的已同步占位及缓存，**包括已固定文件**；本地修改、新建、占用及上传结果不明的内容保留并报告。已删除的路径和父目录不会因本地补扫或重启自动重新上传；这次不增加后台远端轮询。
5. “移除挂载”提供“下载并移除”“清空并移除”和“取消”。已注册根的下载并移除保留全部剩余本地文件；从未注册成功的条目显示“移除挂载（保留本地文件）”，离线保留本地内容并移除配置。清空并移除永久删除根目录内全部内容，包括本地修改及新建文件，保留空根目录，不下载或删除文件源内容；提交前再次确认，默认取消。

移除失败或取消后会保留模式与状态，重启不会自动重放删除操作；再次选择相同方式重试。已经删除的内容不会因取消而恢复。

常规缓存操作会保护本地修改、新建、删除和重命名；明确选择清空时按上述范围删除。本版本自动上传新增文件、目录和文件修改；最后一次变化后默认等待 2 秒，连接恢复、监控溢出及每 60 秒补扫。**本地删除不删除远端；重命名上传新路径，保留远端旧项。** 完整双向同步另行交付，远端变化通过“刷新”或重连取得。WebDAV 使用独立 HTTP 客户端与 Depth 1 枚举；S3 使用 AWS SDK v4、SigV4 签名及分页，路径寻址默认开启。默认 HTTPS，原子替换等未验证的能力保持未知。WebDAV 的可选空／非标准 ETag 不阻断枚举；缺少可靠标签时使用整文件快照缓存，可靠强标签仍严格验证版本。系统状态为本地（实心绿勾）、仅远端（云）及待脱水（空心绿勾，允许回收）。设置页可调整默认 10 GiB 的缓存软上限，0 关闭自动回收；固定、修改和活动内容不会为了达标而被回收。

上传使用挂载目录外的不可变快照，每根同时上传一个文件、全局最多两个；发送阶段持久化，结果不明时先只读核对远端；目标不存在且无法区分未写入与写入后被删除时，保留冲突，由用户显式重试或另存副本。取消本代后不会立即自动重传，可手动重试或再次修改。上传期间继续编辑会保留下一代；只有本地身份与内容仍匹配快照时才更新同步状态。断开、退出及移除会先停止调度并结束在途处理。

WebDAV 不创建辅助目录，必要的探测及安全上传临时文件保存精确 SQLite 日志；完成后清理，失败时在协议日志提示，重连核对重试，外部修改受到保护。WebDAV 与 S3 分别验证创建、更新及其他写能力，“测试连接”仍只读。无可靠条件或已知基线时拒绝覆盖，保留本地修改和远端内容。当前坚果云实测可通过临时上传及 `MOVE Overwrite:F` 安全新增，但其独占锁约束未通过验证，**现有文件更新会提示冲突，请在任务页“另存副本”**；仅声明支持 LOCK 不足以允许覆盖。S3 保留 SigV4、条件 PUT、条件分片完成及回执核对，ETag 不作为 MD5。

读取会合并同时发生的整文件快照和元数据查询，取消一个请求不影响其他等待者，明确提交的下载优先并保留普通读取机会。首次打开大文件仍受服务速度及完整快照要求影响。每个挂载有默认关闭的“缩略图（实验性）”开关，支持 JPEG／PNG／BMP／GIF 首帧、MP4/H.264 视频帧和 EXE 内嵌图标。优先缓存和完整本地内容；远端只进行严格版本及范围校验的有界预览（8 MiB／32 次／10 秒），不水合原占位，也不退化到整文件缓存。不满足能力或预算时显示普通图标；坚果云当前需先显式下载完整内容。关闭会停止本挂载预览，已有缓存保留。正式 MSIX 暂不自动注册处理器；使用 -ExperimentalThumbnails 生成的实验包可注册，实际 Explorer 各显示路径仍待验收。

关闭窗口默认隐藏到托盘。`--tray` 或 `--background` 可以静默启动，主窗口按需创建；普通重复启动恢复已有窗口。真正退出仅断开 CFAPI，保留同步根注册，未下载文件需再次启动应用才能读取。默认随程序启动连接挂载，Windows 登录启动仍默认关闭。

配置位于 `HKCU\Software\HybridMount.NET`，挂载定义在其 Mounts/Packaged 或 Mounts/Unpackaged 子键。运行索引和任务使用 SQLite，凭据按当前 Windows 用户 DPAPI 加密保存在 Mounting/Credentials，整文件退化缓存位于 Mounting/RangeCache，图标缓存在 Mounting/PreviewCache，上传快照位于 Mounting/UploadStaging，这些缓存均计入软上限；待上传、冲突或结果不明的内容及其快照受到保护，可能暂时超过软上限。旧模拟源保留在 Mounting/MockSources，仅供旧挂载迁出；不自动改绑 WebDAV。打包数据位于本包 LocalState，非打包数据位于 `%LOCALAPPDATA%\HybridMount.NET`；调试使用独立挂载目录，不接管另一模式缺少配置的根。

日志页提供筛选、复制、导出与清除，最近显示 100 条，保留最多 30 天／10,000 条。主题、窗口背景和关闭行为可以在设置中调整。

生命周期诊断位于数据目录的 `Logs/lifecycle-*.jsonl` 和 `Logs/last-session.json`，保留最近 32 个会话；记录启动、退出阶段及未处理异常的类型、HRESULT 和安全堆栈，不保存异常原始消息或服务响应。缺少退出完成记录仅表示原因待定位，不能单凭 Visual Studio 的 `0xffffffff` 输出认定崩溃。通知合并和普通存在性查询不再通过重复抛出自有异常完成；SDK 自身已捕获的首次异常仍可能出现在调试输出中。

## 构建

安装 .NET SDK 10.0.401、Visual Studio 2026 的 C++ 桌面／ARM64 工具链、Windows SDK 10.0.26100.0 和 MSIX 工具。打开根目录的 **HybridMount.slnx**；解决方案仅包含以下三个工程：

| 工程 | 职责 |
| --- | --- |
| HybridMount | 界面、配置、生命周期和 NamedPipe 服务；输出 HybridMount.NET.exe |
| HybridMount.Ext | 原生资源管理器菜单与候选云缩略图处理器 |
| HybridMount.Tests | 生产服务契约及真实原生 DLL 集成测试 |

Visual Studio 调试选择：

| 运行方式 | 解决方案配置 | 启动配置 |
| --- | --- | --- |
| 打包调试 | Debug | HybridMount (Packaged) |
| 直接 EXE 调试 | DebugUnpackaged | HybridMount (Unpackaged) |

Release／ReleaseUnpackaged 同理。非打包配置设置 `WindowsPackageType=None`，可直接运行 EXE；扩展、测试工程在 Unpackaged 配置中复用对应 Debug／Release 构建。输出按架构、配置、Packaged／Unpackaged 隔离。

在根目录使用 PowerShell 7：

```powershell
# 依次构建扩展和主程序，再执行 x64 测试
.\build\Build.ps1 -Platform x64 -Configuration Debug -Deployment Unpackaged -Test

# 自包含 MSIX；-Package 默认选择 Packaged；不提供证书参数时输出未签名包
.\build\Build.ps1 -Platform x64 -Configuration Release -Package
.\build\Build.ps1 -Platform ARM64 -Configuration Release -Package

# 使用当前用户证书存储中的匹配 Publisher 证书签名并验证
.\build\Build.ps1 -Platform x64 -Configuration Release -Package -CertificateThumbprint '<证书指纹>'

# 显式生成注册缩略图处理器的实验包；每挂载仍默认关闭
.\build\Build.ps1 -Platform x64 -Configuration Release -Package -ExperimentalThumbnails -CertificateThumbprint '<证书指纹>'

# 从本项目矢量母版重新导出 PNG / ICO
.\build\Update-Assets.ps1
```

正式产物位于 `artifacts\packages\<架构>`，实验缩略图包位于 `artifacts\packages-preview\<架构>`，构建日志、TRX 与包哈希位于 `artifacts\validation`。ARM64 可以交叉构建，测试运行必须在 ARM64 主机进行。直接构建主工程前，需要先构建同架构、同配置的扩展；推荐使用上述脚本或 slnx。

已签名且证书受本机信任时，可通过 `Add-AppxPackage -Path '<包路径>'` 安装。MSIX 同时注册菜单和 StartupTask；不需要手工使用 regsvr32。未打包开发运行不注册 Explorer 菜单，登录启动使用当前用户 Run 项。

验证细节及待测项目见 [docs/Validation-0.0.5.1.md](docs/Validation-0.0.5.1.md)，迁移来源见 [docs/Migration-0.0.1.md](docs/Migration-0.0.1.md)。路线图保存在根目录 HybridMount-devplan.md。AWS SDK 采用 Apache-2.0，随包附带 ThirdParty/AWSSDK/License.txt 和 Notice.txt。
