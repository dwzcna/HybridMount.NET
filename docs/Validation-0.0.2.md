# 0.0.2 验证记录

2026-10-29。代码实现与验收结果分别记录。人工估算仍为 40–64 小时、5–8 个工作日。本轮于北京时间 21:11:55 开始，截至 22:18:30 已用约 67 分钟；真实 CFAPI ABI／脱水排查、发布签名和启动复测使完整交付超过约 40 分钟的初始目标。

## 已测试

| 架构／模式 | 实际结果 | 证据（artifacts/validation 下） |
| --- | --- | --- |
| x64 Release 自动化 | 19/19 通过，失败和跳过均为 0 | 002-release-final-x64.trx |
| x64 Debug 自动化（前批实现） | 19/19 通过；最终保护修订由 Release 全量复测 | 002-tests-x64.trx、contracts-x64-Debug.trx |
| x64 Debug Unpackaged | 最终程序构建通过 | managed-x64-Debug-Unpackaged.log |
| x64 实际 WinUI 进程 | 静默无主窗口、静默重复启动不激活、普通重复启动恢复窗口、拒绝非挂载目录缓存请求且文件不变 | launch-x64-Unpackaged.txt |
| x64 Release MSIX | 自包含主程序、原生扩展、包生成、签名及包内容检查通过 | managed-x64-Release-Packaged.log、signature-x64.log、package-x64.sha256.txt |
| ARM64 Release MSIX | 交叉构建、原生扩展、签名和包内容检查通过；未运行 ARM64 测试 | managed-ARM64-Release-Packaged.log、signature-ARM64.log、package-ARM64.sha256.txt |

源契约测试覆盖持久身份、范围读取、版本冲突、条件替换、目录移动／删除、重开数据库、取消及 Windows 名称拒绝。原生测试实际加载扩展 DLL，验证 COM 枚举和 v2 NamedPipe 往返。

真实 Windows NTFS／Cloud Files 驱动测试执行了 WinRT 注册、真实占位创建、打开触发读取、固定、脱水后重新读取、本地修改保护、本地新增保留、删除与重命名后重连、宿主重建恢复，以及完整下载并保留普通文件的移除。32 MiB + 37 字节文件验证跨页位置及末尾内容；解除后扫描确认没有残余云占位。没有用普通空文件代替 CFAPI。测试仅使用独立随机 NTFS 目录和 HKCU 测试子键。

原生 SDK 静态断言与托管 ABI 测试核对根身份偏移、文件身份偏移及回调／操作结构。包检查覆盖版本、PE 架构、自包含运行时、骡子资源、CloudFiles 扩展、菜单与 StartupTask 声明。

首次 Release 全量复测曾因正在运行的应用占用产品管道而失败；保留原实例，管道释放后最终 19 项全部通过。启动验证首次等待 5 秒超时，应用随后正常就绪；脚本加入最多 30 秒的就绪重试，并复用本轮创建的静默测试进程完成其余验证。该进程保留供手工验收，没有终止已有挂载。

## 已生成产物

使用当前用户开发证书 CN=dwzcn 签名，证书指纹为 3FB1D78271BA8F6EDD3CE163F57D93D13FEB83C0；signtool verify /pa /all /v 均通过。

| 架构 | 项目根目录下的包路径 | SHA-256 |
| --- | --- | --- |
| x64 | artifacts/packages/x64/HybridMount_0.0.2.0_x64_Test/HybridMount_0.0.2.0_x64.msix | CC1BBC0AFD24594CFC8E15C6CD63091E387121E8684ADC7BE539693442EE29A2 |
| ARM64 | artifacts/packages/ARM64/HybridMount_0.0.2.0_arm64_Test/HybridMount_0.0.2.0_arm64.msix | D5BF2D71A7E618B7DC75FC5E85F094048A6B078F9DDEEBD6EC5D732E2F5C2F7A |

## 待测试

- 完整 GUI 操作、MapDrive 导航布局、选择器、任务结果窗口与不同主题下的视觉验收。
- 安装最新 MSIX 后，在真实 Explorer 中验证文件／目录／空白处、同根多选、混合根隐藏、应用关闭时自动激活及逐项结果。
- 最终 MSIX 安装、从 0.0.1 升级及卸载；打包身份下的完整挂载流程。
- 登录启动、Explorer 重启、系统注销／断电恢复及跨会话占用。
- 文件并发修改与脱水竞争、大目录、磁盘耗尽、拒绝访问、系统驱动故障及压力下的取消。
- 异常丢失／破坏同步根注册、陌生云占位残留、移除中途崩溃的故障注入；保护代码已实现，故障分支不记为通过。
- 未来驱动对未知／不支持能力的拒绝与降级；当前仅模拟源声明已验证的能力。
- ARM64 实机安装、运行和 Explorer 扩展；交叉构建和包架构检查不视为实机通过。

桌面自动化工具先前因运行内核启动失败未能执行完整视觉验收，不能把进程元数据验证当作界面与 Explorer 验收。

## 实现边界与恢复

- 主程序只使用 StorageProviderSyncRootManager.Register 注册同步根；CfConnectSyncRoot 负责连接。常规退出不注销，移除在内容完整、解除全部云占位后才注销。
- 模拟源以不可变内容文件和 SQLite 指针切换提供条件写与原子替换；旧内容版本暂不自动回收。本版不将用户本地编辑写回。
- SQLite 索引保留删除、重命名和修改保护。已创建标志在原生成功后持久化；重启先扫描实际身份，防止把未完成创建当作用户删除。
- 移除流程持久化 RemovalPending，先完成所有内容水合，再逐项解除占位；中断后保留普通文件和剩余占位，可继续移除，不自动重放旧任务。注册丢失或仍有云文件时保留配置。
- IPC v2 返回任务 ID，状态可查询和取消；v1 仅兼容激活，旧菜单文件请求返回未执行。根身份、用户及路径在操作前重新验证。
- GetState 仅查本机 CFAPI 元数据；快速调用返回 E_PENDING，慢速回调进行查询，不连接管道、不读取内容。
- 0.0.1 配置和日志保留，新增挂载定义按 Packaged／Unpackaged 隔离，运行索引使用独立 schema 1。旧版程序与新版互斥，避免同时接管。

## 复现

先退出正在运行的应用，避免占用集成测试管道：

```powershell
.\build\Build.ps1 -Platform x64 -Configuration Debug -Deployment Unpackaged -Test
.\build\Build.ps1 -Platform x64 -Configuration Release -Deployment Packaged -Package -Test
.\build\Build.ps1 -Platform ARM64 -Configuration Release -Deployment Packaged -Package
.\build\Test-Launch.ps1 -ExecutablePath '.\HybridMount\bin\x64\Debug\Unpackaged\net10.0-windows10.0.26100.0\win-x64\HybridMount.NET.exe'
```

首次真实测试发现并修复：操作联合体完整大小为 48 字节、磁盘范围枚举从 1 开始，以及用于检查范围的写句柄会触发水合。修复后使用仅读属性句柄验证实际脱水；失败测试留下的自有同步根通过保留数据移除恢复，没有直接删除未下载云文件。
