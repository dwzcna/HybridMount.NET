# 0.0.1 迁移与边界

参考项目仅作为本版源码来源，HybridMount 独立构建，不链接参考仓库工程，也不复制其图像。0.0.2 起新功能在本项目内部独立开发。

| 来源 | 迁移内容 | 本项目改动 |
| --- | --- | --- |
| D:\src\dzone\toolkit\EPaperKit.Ax | App.xaml 公共样式、外观模型／服务／ViewModel、Thin Acrylic、SQLite 日志与通知区域基础 | 命名空间、配置／日志根、独立模型、资源和退出集成；移除设备、蓝牙及旧业务 |
| D:\src\ProKit\ProKit.Ext | Microsoft ExplorerCommand 示例衍生的 COM 类工厂、菜单和模块计数模式 | 根菜单与两项占位命令、独立枚举器、背景路径、NamedPipe、异步线程及双架构；保留相关版权声明 |

本轮没有修改这两个参考目录。主工程只引用 NuGet 依赖，扩展使用 Windows SDK／MSVC，无 Boost 或 C++/WinRT 额外依赖。

全部图形均来自本项目 `HybridMount/Assets/HybridMount.svg` 的骡子设计，`build/Update-Assets.ps1` 使用 WPF 确定性导出 PNG／ICO。不是原项目换名的图形，也未调用外部图像服务。若以后替换品牌图形，先修改母版再运行导出脚本。

## 内部接口

- AppearancePreferences／IAppearancePreferenceStore／AppearanceSettingsService：正式快照只在持久化成功后更新。外观分别使用两个 DWORD，未知值按字段回退；读取失败不覆盖现有值。
- StartupService／IStartupService：MSIX 的 HybridMountStartup 由 Windows StartupTask 管理；未打包使用 HKCU Run 的 HybridMount.NET。StartupEnabled 只缓存实际结果，不作为系统状态的替代。
- LoggerService：有界队列串行写入／读取／清除 SQLite；真实退出排空已接纳任务。队列满会报告未保存，不承诺所有高频日志均成功持久化。
- SingleInstanceService：当前用户、会话隔离的互斥量。持有线程取得和释放；第二实例通过 IPC 恢复窗口或静默检查。
- IpcProtocol：`HybridMount.NET.v1.<SID>.<SessionId>`，4 字节小端长度加 UTF-8 JSON；最大 1 MiB、最多 1024 路径、版本 1、GUID 关联。操作为 ping／showWindow／freeSpace／keepOnDevice，响应为 ready／accepted／notImplemented／invalidRequest。
- IpcServerService：CurrentUserOnly 的四路异步管道，每个已连接请求最多五秒。扩展不在 GetState 中访问文件、网络或管道；Invoke 复制文件系统路径后转工作线程，必要时静默激活应用。

扩展 COM CLSID：`F4A8FD0B-4055-4F23-8CC0-91B736B3E491`。MSIX 注册 `*`、`Directory`、`Directory\Background`。本版不注册同步根、覆盖图标处理器或系统服务。

## 升级

产品版本集中在 Directory.Build.props，程序集与 MSIX 使用四段版本。后续升级必须递增包版本，并保持包身份、Publisher、App Id、StartupTask Id 和 COM CLSID 的兼容性。当前 SchemaVersion 为 1；读取设置不扫描或导入参考工程键。

0.0.2 将以本产品挂载根筛选菜单，并接入模拟源与 CFAPI。0.0.3 删除生产模拟协议前应识别旧挂载、保留本地数据并提供显式迁出／清理；此项尚未实现。首版没有挂载目录或缓存数据可迁移。
