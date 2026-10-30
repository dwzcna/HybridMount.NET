# HybridMount 0.0.4.3 验收记录

验证日期：2026-10-30。执行主机为 Windows x64，.NET SDK 10.0.401、Visual Studio 2026 C++ 与 Windows SDK 10.0.26100.0。开发及自动验证预算 4–8 小时，真实服务传输与桌面环境等待另计。本文的“已完成／未完成”仅描述代码；测试结果与实现状态分开维护。

## 已完成

- 挂载目录本地新增文件／目录及文件修改自动上传；删除不传播，改名上传新路径并保留远端旧项。FileSystemWatcher、CFAPI 关闭通知只唤醒调度，默认 2 秒稳定等待，连接／监控溢出及每 60 秒补扫，不主动下载干净占位正文。
- 新挂载默认开启；旧挂载首次完整扫描和多选确认，默认不选，未选项保持保护。纯属性变化不授权旧改动上传。
- 每根串行、全局两个上传；不可变快照位于挂载根外，核对身份、USN、摘要和代次，发送前复核持久快照。写占用等待，部分修改占位核对旧版本后显式补齐，网络期间不锁原文件。
- SQLite 持久上传阶段和基线。结果不明先只读核对，不盲目重发；上传中再修改保留下一代；取消同代后需要重试或实际修改。断开、退出、凭据更新及两种移除先停止上传，再按根锁清理。
- 上传事务明确提交索引／基线／队列，代次仍一致才解除保护；普通文件原地转换为完整本地占位并保留固定状态。未决内容及快照禁止脱水和回收。
- 挂载页增加自动上传及旧改动清单；任务页增加文件、字节进度、等待／冲突／结果待核对，支持取消、重试、另存新远端路径副本。
- 写能力拆分。WebDAV 条件 PUT、有效独占锁／续租＋原始摘要分别验证；无可靠条件时停止覆盖。创建增加经验证的临时 PUT＋MOVE Overwrite:F。S3 分别验证条件 PUT／条件分片完成，保留已知 UploadId 恢复；严格核对回执 ETag，可查询时核对 VersionId，不把 ETag 当 MD5。
- 保留三个工程、IPC v2、按需读取、共享下载、EXE 图标缓存及缩略图启用门槛。主程序、原生扩展、测试和 MSIX 统一 0.0.4.3，由集中版本生成打包清单。

## 未完成

本轮批准范围内无已知待实现代码。完整双向同步、删除传播、远端事件通知和自动冲突合并留待后续；SMB／NFS 无排期，保留协议扩展接口。坚果云现有文件覆盖因服务未通过安全写入验证而受限，不绕过条件进行覆盖。

## 待测试

- 真实 GUI 的自动上传开关、旧改动多选、任务进度／取消／重试／副本对话框，以及 Explorer 状态和不同编辑器的实际交互。
- MSIX 安装、升级、卸载及打包身份挂载；ARM64 实机运行。交叉构建与签名不等同于 ARM64 实机验收。
- 真实大对象分片、长时间锁续租、网络断线／响应丢失／异常退出压力、多文件并发与 ACL 变化。
- 更多 WebDAV、AWS S3、MinIO 及其他兼容端点的创建／覆盖能力。当前 R2 成功不能证明历史 CSTCloud 401 已修复。
- Explorer 小／大图标、预览窗格和其他类型展示，候选云缩略图处理器仍默认不注册。

## 已测试

证据位于 `artifacts/validation/`，产物与本机日志保持忽略，不包含凭据、授权头或锁令牌。

| 范围 | 实际结果 | 证据 |
| --- | --- | --- |
| x64 Debug／Unpackaged | 最终 137/137，零失败／跳过 | 0043-all-x64-Debug.trx |
| x64 Release | 最终 137/137，零失败／跳过 | 0043-all-x64-Release.trx |
| 新增自动流程回归 | 12 项：文件／目录、零字节／中文、连续保存／替换／改名、上传中编辑、首次选择／属性保护、取消重试、响应丢失／重启核对、占用／外部链接、离线同名、大小写改名、断开重连、未连接首次确认、损坏快照保护；部分测试合并多个场景 | UploadFlowTests；以上全量 TRX |
| 新增协议回归 | 8 项：写操作能力独立、非标准 DAV 标签与锁、基线缺失／远端变化／条件忽略、无效锁拒绝、S3 无删除／复制权限仍可上传、PUT 独有 VersionId 兼容及不同回执拒绝 | UploadProtocolTests；以上全量 TRX |
| 原功能回归 | 原有协议、真实 CFAPI、IPC／原生 COM、读取／脱水／缓存、保护与移除回归全部通过 | 以上全量 TRX |
| 实际非打包启动 | --tray 启动、IPC ready、重复静默实例退出 0，已验证进程保留托盘；主程序、双架构扩展及 Release 测试二进制版本均 0.0.4.3 | 0043-launch-x64-Unpackaged.json |
| x64／ARM64 正式包 | 自包含、四段版本、PE 架构、全新资源、依赖及 CloudFiles／启动／菜单声明校验通过，签名验证通过，默认不注册候选缩略图 | 0043-signature-x64.log、0043-signature-ARM64.log、0043-final-validation.json |
| GUI 工具 | 初始化及重置重试失败，没有执行 GUI 输入；此项为失败记录 | 0043-gui-attempt.txt |

### 真实挂载的文件系统闭环

使用原程序内的两个实例，凭据只通过当前用户 DPAPI 解密到内存。由本地文件系统变更触发监控、队列、驱动上传和 CFAPI 状态提交，不以直接调用上传驱动替代完整闭环。

| 服务／本地根 | 新增、目录、中文和零字节 | 已有文件修改 | 重启恢复与读回 SHA-256 |
| --- | --- | --- | --- |
| 坚果云 WebDAV／D:\wdavtest | 在“我的坚果云”下唯一目录通过；账户根 MKCOL 返回 400 | **未通过安全覆盖能力验证**，保留冲突；另存新副本成功，原远端内容不变 | 通过 |
| Cloudflare R2／D:\s3test | 唯一目录通过，HTTPS／SigV4／Path-Style，使用已保存区域 | 通过 | 通过 |

坚果云 OPTIONS 声明 LOCK／UNLOCK，但实测第二个不同持有者的锁仍成功、无锁令牌 PUT 也成功，不能据此安全覆盖。非标准 ETag 不升级为强标签；已验证 MOVE Overwrite:F 拒绝同名目标，故允许安全新增。文件更新保留两端并提示另存副本。证据：`0043-dav-driver-live.json`、`0043-dav-lock-format.json`、`0043-real-filesystem-upload.json`。

R2 初轮发现 PUT 返回 VersionId 而 HEAD 不公开它，导致回执误报未知。修复后仍严格匹配 ETag，仅在可查询时校验 VersionId；真实文件系统复测新增、修改及重启恢复均直接完成，无误报 OutcomeUnknown。证据：`0043-r2-receipt.json`、`0043-real-r2-receipt-fixed.json`。此兼容不允许把随后不同 ETag 的对象记为本次回执。

### 测试范围与清理

本轮主验收目录如下，其本地及远端文件均按清单清理成功；没有通过测试表单批量选择原有用户改动：

- `0043-real-filesystem-upload.json` 记录的两个 `HybridMount-0043-*` 唯一目录。
- R2 最终复测目录 `HybridMount-0043-409725544cb44b8c9ecd6100930d7679`；记录于 `0043-real-r2-receipt-fixed.json`。
- 独立 R2 回执诊断对象清理成功，记录于 `0043-r2-receipt.json`。

前期探测有一处远端残留，文件及父目录 DELETE 持续返回 409；最终只读 lockdiscovery 未发现可释放锁，没有扩大清理范围：

```text
我的坚果云/HybridMount-upload-check-5e4cc11f6ace46319761b7b9a193a6fe/
  .hybridmount-probe-52d3c421864b41fa8f809d0888749ad1
```

对应本地占位曾安全清理，后续刷新可能重新显示远端残留。另一前期目录 `HybridMount-lock-format-bc64fd310c8b4f3f9d0dc66f98fc530c` 已删除。证据：`0043-early-probe-cleanup-final.json`、`0043-early-probe-lock-cleanup.json`。不记为全部清理成功。

测试收尾在 18:03 恢复了两个旧挂载“自动上传关闭／等待首次确认”，见 `0043-real-settings-restored.json`；最后一次 R2 专项也包含恢复步骤。最终 18:48 启动观察时，两个设置均已开启／确认；日志另有 18:41 的新版程序启动及两个旧 S3 任务完成记录，发生在本轮启动核对前。保留这些后续保存的实际配置，不用旧快照覆盖；这不是上传验收脚本选择旧改动的证据。当前设置及时间写入 `0043-launch-x64-Unpackaged.json`、`0043-final-validation.json`。

### 构建与产物

本轮最终执行：

```powershell
.\build\Build.ps1 -Platform x64 -Configuration Debug -Deployment Unpackaged -Test
.\build\Build.ps1 -Platform x64 -Configuration Release -Deployment Packaged -Package -Test -CertificateThumbprint '<当前用户证书指纹>'
.\build\Build.ps1 -Platform ARM64 -Configuration Release -Deployment Packaged -Package -CertificateThumbprint '<当前用户证书指纹>'
```

签名产物：

| 架构 | 路径 | SHA-256 |
| --- | --- | --- |
| x64 | artifacts/packages/x64/HybridMount_0.0.4.3_x64_Test/HybridMount_0.0.4.3_x64.msix | B492DCA880B0B99EEF986D835AB699D37685DC8E9764360635B827EB29F1FAA2 |
| ARM64 | artifacts/packages/ARM64/HybridMount_0.0.4.3_arm64_Test/HybridMount_0.0.4.3_arm64.msix | 759C190074D00C2C719DE25AE5E2500D511CA79AF0335F378304098AA7C02773 |

首次打包因静态清单旧版本生成错误名称，修复后重新完成两架构构建与验证；本轮错误的 0.0.4.2 名称产物已隔离到 `artifacts/validation/0043-failed-package-version/`，不能作为有效 0.0.4.2 发布包。最终签名验证通过，开发证书签名未加时间戳。
