# HybridMount.NET

当前版本 **0.0.2**，面向 Windows 11 21H2 及以上，支持 x64／ARM64，使用 WinUI 3、.NET 10、MSIX 与 Windows Cloud Files API。

导航为“首页 → 挂载 → 日志”，设置位于底部。挂载使用 MapDrive 图标；品牌资源继续使用本项目的骡子图形。

## 使用

1. 在挂载页展开“新增挂载”，填写名称并选择本地 NTFS 空目录，点击“创建并连接”。目前只有内置模拟文件源，无需服务器或网络。
2. 在资源管理器浏览真实占位文件，打开时按需读取；“始终保留在设备上”固定并下载，“释放空间”解除固定并回收合格缓存。本地修改、新建、活动文件和状态不明项目受到保护。
3. 挂载页支持连接、断开、刷新、打开目录和后台任务查询／取消／逐项结果。移除挂载会先完整下载剩余文件，再解除占位，保留普通本地目录；失败可重试，不自动删除内容。

本地修改、删除和重命名会保留，**本版本不自动上传或双向同步**。网络协议、自动缓存回收和状态显示完善按后续版本交付。

关闭窗口默认隐藏到托盘。`--tray` 或 `--background` 可以静默启动，主窗口按需创建；普通重复启动恢复已有窗口。真正退出仅断开 CFAPI，保留同步根注册，未下载文件需再次启动应用才能读取。默认随程序启动连接挂载，Windows 登录启动仍默认关闭。

配置位于 `HKCU\Software\HybridMount.NET`，挂载定义在其 Mounts/Packaged 或 Mounts/Unpackaged 子键。运行索引和任务使用 SQLite，模拟源内容及版本保存在本产品数据目录的 Mounting/MockSources。打包数据位于本包 LocalState，非打包数据位于 `%LOCALAPPDATA%\HybridMount.NET`；调试使用独立挂载目录，不接管另一模式缺少配置的根。

日志页提供筛选、复制、导出与清除，最近显示 100 条，保留最多 30 天／10,000 条。主题、窗口背景和关闭行为可以在设置中调整。

## 构建

安装 .NET SDK 10.0.401、Visual Studio 2026 的 C++ 桌面／ARM64 工具链、Windows SDK 10.0.26100.0 和 MSIX 工具。打开根目录的 **HybridMount.slnx**；解决方案仅包含以下三个工程：

| 工程 | 职责 |
| --- | --- |
| HybridMount | 界面、配置、生命周期和 NamedPipe 服务；输出 HybridMount.NET.exe |
| HybridMount.Ext | 原生 IExplorerCommand 资源管理器扩展 |
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

# 从本项目矢量母版重新导出 PNG / ICO
.\build\Update-Assets.ps1
```

产物位于 `artifacts\packages\<架构>`，构建日志、TRX 与包哈希位于 `artifacts\validation`。ARM64 可以交叉构建，测试运行必须在 ARM64 主机进行。直接构建主工程前，需要先构建同架构、同配置的扩展；推荐使用上述脚本或 slnx。

已签名且证书受本机信任时，可通过 `Add-AppxPackage -Path '<包路径>'` 安装。MSIX 同时注册菜单和 StartupTask；不需要手工使用 regsvr32。未打包开发运行不注册 Explorer 菜单，登录启动使用当前用户 Run 项。

验证细节及待测项目见 [docs/Validation-0.0.2.md](docs/Validation-0.0.2.md)，迁移来源见 [docs/Migration-0.0.1.md](docs/Migration-0.0.1.md)。路线图保存在根目录 HybridMount-devplan.md。
