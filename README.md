# HybridMount.NET

当前版本 **0.0.1**，面向 Windows 11 21H2 及以上，支持 x64／ARM64。主程序使用 WinUI 3、.NET 10 和 MSIX；命名空间为 `HybridMount`。

本版提供首页、日志和设置界面；支持跟随系统／浅色／深色主题、五种窗口背景、注册表配置、SQLite 日志、常驻托盘及登录启动。新应用、托盘和安装包均使用本项目的骡子主题图标。

资源管理器的文件、目录和目录背景菜单中提供 HybridMount.NET，包含“释放空间”“始终保留在设备上”。**0.0.1 的命令只记录请求并提示尚未实现，不修改文件。此版本没有网络协议或 CFAPI 挂载。**

## 使用

- 关闭窗口默认隐藏到通知区域；最小化保留任务栏行为。托盘双击或“显示主窗口”可以恢复，托盘“退出 HybridMount.NET”会真正退出。
- 设置页可改变关闭行为。登录启动默认关闭，开启后静默进入托盘；系统策略或用户在任务管理器中的禁用会反映到实际状态。
- 普通重复启动恢复已有窗口，`HybridMount.NET.exe --tray` 静默启动或检查已有实例。
- 日志页显示最近 100 条，可筛选、复制、导出和清除；数据库保留最多 30 天／10,000 条，重复消息限频。
- 配置位于 `HKCU\Software\HybridMount.NET`。开发运行的数据位于 `%LOCALAPPDATA%\HybridMount.NET`，MSIX 使用本包的 `LocalState`，日志位于其 `Logs\diagnostics.db`。

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

验证细节及待测项目见 [docs/Validation-0.0.1.md](docs/Validation-0.0.1.md)，迁移来源见 [docs/Migration-0.0.1.md](docs/Migration-0.0.1.md)。路线图保存在根目录 HybridMount-devplan.md。
