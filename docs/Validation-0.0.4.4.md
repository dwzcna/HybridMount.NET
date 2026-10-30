# HybridMount 0.0.4.4 验收记录

验证日期：2026-10-30。执行主机为 Windows x64，使用 .NET SDK 10.0.401、Visual Studio 2026 C++ 与 Windows SDK 10.0.26100.0。开发与验证预算 2–4 小时，真实服务及桌面环境等待另计。实现与验收分开维护。

## 已完成

- 用户新 SVG 的图形纳入原母版；WPF 导出器按 viewBox 缩放，支持分组、样式继承、线性／径向渐变、裁剪、透明度和变换，未支持图形明确失败。全部 37 张 PNG 和 11 帧 ICO 更新，引用与文件命名沿用本项目。
- 挂载索引增加独立 `RemotePresence`：Unknown／Present／Deleted。SQLite v3 使用独立表保存远端状态，旧九列索引无损兼容、默认 Unknown；本地删除标记保持独立。
- 刷新及重连先完整收集远端快照，再确认缺失，之后才应用删除。认证、权限、网络及不完整响应失败时不执行清理。
- 在根锁内检查身份和上传状态，使用排他保护句柄按句柄删除自家占位；不水合，不跟随外部链接，不删除根。所有未修改的已同步项均随确认的远端删除清理，包含已固定文件；目录只从子到父清理空目录。
- 本地修改、新建、占用和未决上传保留并报告。持久删除屏障阻止关闭通知、补扫、重启和父目录创建恢复旧路径；目标更新不退化成创建。快照校验后在根锁内再次核对删除屏障、上传开关和任务代次，进入发送阶段；刷新先设置冲突时不得被发送线程覆盖。未知结果且目标缺失时要求显式重试或另存副本。
- 远端再次出现的条目恢复新占位和版本；刷新／重连保存逐项结果。任务显示文件、目录及副本目标；成功一代上传在新意图产生前事务归档，后续冲突不覆盖原成功事实。
- 通知生产者在共同短锁内合并信号，停止后忽略迟到通知，线程结束后释放；不以 SemaphoreFullException 处理正常事件。重试不推迟原来的稳定时间。
- 增加兼容 `IFileSource.TryGetMetadataAsync`；存在时返回条目，仅明确不存在返回空。S3 能力探测的预期拒绝走结果分类，真实冲突、认证、网络及结果不明仍保留失败语义；不完整存在性响应不能确认删除。
- 生命周期记录启动、退出和未处理异常的类型／HRESULT／安全堆栈，保留最近 32 个会话，不保存异常原始消息、Data、凭据或服务响应，不设置异常已处理标志。未完成会话只提示原因待定位。
- 保留三个工程及 IPC v2；快照模型增加可选目录类型／远端目标，兼容旧消息与历史。主程序、扩展、测试及 MSIX 统一 0.0.4.4。

## 未完成

本轮批准范围内无已知待实现代码。完整双向同步、本地删除向远端传播、后台远端轮询／通知及自动合并留待后续。SMB／NFS 不排期，统一协议接口保留。

原始 `0xffffffff` 退出原因未复现，不能将日志补充记为已修复该次崩溃；当前坚果云安全覆盖仍受服务能力限制，继续使用冲突和另存副本。

## 待测试

- 真实 GUI 的任务目标／逐项结果、托盘图标及正常退出，Explorer 新图标、缓存刷新、状态展示和高 DPI／主题组合。
- 原始异常退出的实际复现、现场堆栈及根因；Visual Studio 的已捕获首次异常和线程返回 0 本身不作为崩溃证据。
- 跨版本 MSIX 安装升级、ARM64 实机运行、更多 WebDAV／S3 服务、真实大目录、高并发和网络中断压力。
- 既有候选云缩略图的实际 Explorer 启用门槛；正式 MSIX 仍默认不自动注册。

## 已测试

证据位于 `artifacts/validation/`，产物、隔离脚本和本机日志保持忽略。

| 范围 | 实际结果 | 证据 |
| --- | --- | --- |
| x64 Debug | 最终 154/154，零失败／跳过，含 Packaged 编译；Unpackaged 也完成最终编译 | 0044-all-x64-Debug.trx、managed-x64-Debug-*.log |
| x64 Release | 最终 154/154，零失败／跳过 | 0044-all-x64-Release.trx |
| 新增远端删除与异常回归 | 14 项：在线／缓存／固定清理、目录修改／新建保护、远端重现、失败不误删、占用重试、外部联接、在途更新、未知创建重启、旧索引迁移、并发通知、预期异常及不完整 S3 查询；发送前原子屏障、开关及代次复核、成功历史保留断言通过 | RemoteDeletionTests；最终全量 TRX |
| 生命周期 | 3 项：敏感消息／Data 不落盘，类型和堆栈保留；未完成与正常完成区分；并发 JSON 有效、IO 失败不掩盖原异常 | 0044-lifecycle-x64.trx、最终全量 TRX |
| 原有能力 | 既有 WebDAV／S3 上传、分片与取消、原生 COM／IPC、CFAPI 水合／脱水、缓存及移除自动化回归通过；本轮未另做真实 R2 上传 | 最终全量 TRX |
| 资源 | 37 张 PNG 尺寸正确且全部不同于 0.0.4.3；ICO 的 16／20／24／32／40／44／48／64／96／128／256 帧有效；母版与用户新 SVG 一致，256px 预览已视觉检查 | 0044-assets.json |
| 最终非打包启动 | 静默启动、静默重复实例、普通重复启动恢复窗口、非挂载目标拒绝且测试文件不变；生命周期 Ready、窗口创建通过 | 0044-launch-x64-Unpackaged.txt、0044-launch-x64-Unpackaged.json |
| 最终打包启动 | x64 签名包安装，真实包身份 Ready、窗口创建通过；安装 DLL 哈希与最终 MSIX 内 DLL 一致 | 0044-launch-x64-Packaged.json |
| 双架构签名包 | x64／ARM64 自包含、四段版本、PE 架构、资源、依赖和扩展声明、签名校验通过；默认不自动注册候选缩略图 | 0044-signature-*.log、0044-package-*.sha256.txt、0044-final-validation.json |
| GUI 工具 | computer-use 初始化及重置重试均失败，没有执行 GUI 输入 | 0044-gui-attempt.txt |

### 真实坚果云删除闭环（最终源码复测）

读取现有 `D:\wdavtest` 挂载的服务器配置及 DPAPI 凭据，使用唯一远端目录和独立本地 NTFS 同步根执行；没有改绑原挂载、改变其自动上传设置或批量选择旧改动。隔离根位于仓库的验证目录，与原两个挂载根不重叠。

```text
远端：我的坚果云/HybridMount-0044-49be15fc48e041d280d95ceaff1469d6
本地：artifacts/validation/0044-live-49be15fc48e041d280d95ceaff1469d6/view
```

实际链路由文件系统变更触发：

1. 在本地写入三个中文文件，经过 FileSystemWatcher、持久队列、协议上传及 CFAPI 同步提交；全部完成，并直接读回远端 SHA-256，与原始内容一致。
2. 将一个文件固定；关闭该隔离挂载的自动上传后修改另一个本地文件。另建一个仅远端测试种子并刷新得到未下载占位。
3. 在远端删除这四个测试文件，再刷新。未修改完整文件、已固定文件和未下载占位均清理；修改内容保留。刷新返回 PartialFailure，对应一项应保留的冲突及三项成功清理，属于预期结果。
4. 开启隔离挂载自动上传，执行本地扫描并等待 65 秒周期补扫；四个远端路径均保持 HTTP 404。
5. 销毁并重建协调器、重连同一持久根；远端没有复活，本地修改仍保留。
6. 仅清空并注销本轮隔离根，再删除唯一远端目录，远端返回 204。本轮没有残留。

证据：`0044-real-webdav-delete.json` 记录摘要、逐项结果、周期补扫、重连及清理；隔离 SQLite 日志保留于对应验证目录。历史 0.0.4.3 的探测残留记录仍在其验收文档，本次未扩展清理范围。

### 安装与进程验证边界

首次安装过同版本的中间测试包，Windows 在替换最终内容时返回 0x80073CFB；ForceUpdateFromAnyVersion 也不能覆盖同版本不同内容。随后确认本轮测试包索引及上传数据为空、原同步根未与该包关联，只替换当前用户的这份测试安装，再安装最终产物。两个原同步根身份前后逐字节摘要一致，证据为 `0044-package-reinstall.json`。这次重装不是跨版本升级验收。

包与非打包测试实例均在完成 Ready 检查后受控结束；非打包实例清理前确认没有未决上传，精确绑定本轮进程对象、路径、启动时间及会话 ID，不按进程名选取其他实例。受控中断会留下未完成会话标记，这是刻意验证，不代表复现了用户原始退出故障。正常托盘退出的实际 UI 链路仍待验收。

一次验证包装器误用了未设置的 LASTEXITCODE，产品断言本身均通过；最终验证改用脚本成功状态并保存独立证据。桌面工具失败不影响公开 IPC／进程级检查，但不能由此声称 GUI 或 Explorer 视觉验收通过。

### 构建与签名产物

最终执行：

```powershell
.\build\Build.ps1 -Platform x64 -Configuration Debug -Deployment Packaged -Test
.\build\Build.ps1 -Platform x64 -Configuration Debug -Deployment Unpackaged
.\build\Build.ps1 -Platform x64 -Configuration Release -Deployment Packaged -Package -Test -CertificateThumbprint '<当前用户证书指纹>'
.\build\Build.ps1 -Platform ARM64 -Configuration Release -Deployment Packaged -Package -CertificateThumbprint '<当前用户证书指纹>'
```

| 架构 | 签名包 | SHA-256 |
| --- | --- | --- |
| x64 | artifacts/packages/x64/HybridMount_0.0.4.4_x64_Test/HybridMount_0.0.4.4_x64.msix | 4F261C2F4B1578DF7FBEF6AA9F654592EB039792EA413D8A2DD6A9E7B190DE1D |
| ARM64 | artifacts/packages/ARM64/HybridMount_0.0.4.4_arm64_Test/HybridMount_0.0.4.4_arm64.msix | 86AE7D4838792D5D39962112AC3C74944A809155D21EBBF3AE67D255E9BCC6E5 |

使用已有 CN=dwzcn 开发证书，签名及内容验证通过，未加时间戳。x64 最终包已安装；本轮启动的测试实例已结束，保留同步根供后续正常启动。ARM64 仅完成交叉构建与包检查，没有实机通过记录。

## 硬盘图形资源补充（2026-10-30）

本次资源维护沿用 0.0.4.4。使用用户提供的 `HybridMount/Assets/HybridMount-HardDisk.svg` 更新固定母版 `HybridMount.svg`，保持现有应用、托盘、扩展和 MSIX 文件引用；硬盘版源文件及 `HybridMount-hy.svg` 均原样保留并提交，两个输入文件的 SHA-256 前后一致。现有 WPF 导出器可直接处理新图，无需新增绘图依赖或修改导出逻辑。

实际执行 `build/Update-Assets.ps1` 以及 `build/Build.ps1 -Platform x64 -Configuration Debug -Deployment Unpackaged`：37 张 PNG 尺寸正确且全部不同于上一提交，全部非空并含透明背景、四角透明；ICO 的 11 帧均有效。浅／深背景的 16／44／256px 预览已检查，硬盘轮廓及骡子图形无明显裁剪。最小 x64 构建通过，输出的 37 张 PNG 和 ICO 与源资源逐字节一致，主程序 EXE 与原生扩展 DLL 均包含全部新 ICO 图帧。

证据：`0044-harddisk-export.log`、`0044-harddisk-inputs.json`、`0044-harddisk-assets.json`、`0044-harddisk-alpha.json`、`0044-harddisk-preview.png`、`0044-harddisk-build.json` 和 `0044-harddisk-*-x64-Debug*.log`，均位于 `artifacts/validation/`。

本次为资源替换，没有复跑协议自动化或重新生成／安装 MSIX；上文的 154 项测试、签名包及安装哈希属于资源替换前的验收，不作为新硬盘图形包的交付记录。真实托盘／Explorer 展示、高 DPI 和 ARM64 实机继续待测试。
