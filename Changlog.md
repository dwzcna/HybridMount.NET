# HybridMount.NET 变更记录

## 0.0.1 — 代码已实现，验收中（2026-10-29）

### 原始要求与确认

- WinUI 3／.NET 10／MSIX，主工程 HybridMount、扩展 HybridMount.Ext、测试 HybridMount.Tests，支持 x64 和 ARM64。
- 首版从 EPaperKit.Ax 迁移外观、日志与托盘；从 ProKit.Ext 参考迁移 ExplorerCommand 基础。后续版本在本项目独立开发。
- 用户配置根路径为 HKCU\Software\HybridMount.NET；默认关闭窗口隐藏到托盘，登录启动默认关闭。
- 全局占位菜单通过 NamedPipe 记录请求，不修改文件，不接入协议或 CFAPI。
- 开发计划采用已完成、未完成、待测试、已测试，前两项仅针对代码。路线图保留到 S3 的 0.0.6。
- 资源重新制作，采用骡子主题；使用 HybridMount.slnx 替代旧方案，支持打包／非打包调试。

### 已实现代码

- 标题栏、首页／日志／设置导航，系统／浅色／深色主题与 None／Mica／Mica Alt／Acrylic／Thin Acrylic 背景。
- 当前用户注册表偏好、SQLite 有界日志队列及查看／筛选／复制／导出／清除入口。
- 原生通知区域图标、窗口隐藏／恢复／退出、Explorer 重启重建图标、单实例与静默激活。
- MSIX StartupTask 与未打包 HKCU Run 登录启动；系统实际状态和策略说明。
- 原生根菜单与两项占位命令，文件／目录／背景注册、独立枚举器、当前用户会话隔离 IPC、超时及取消。
- 独立 SVG 骡子母版、PNG／ICO 导出、x64／ARM64 扩展与自包含 MSIX 构建、签名和内容校验脚本。
- Debug／Release 打包配置与 DebugUnpackaged／ReleaseUnpackaged 直接运行配置，两项 VS 启动配置，隔离构建输出。

### 实际验证

- x64 Debug／Release 自动化测试 11/11 通过；覆盖配置失败、日志队列、帧边界、服务恢复、单实例及真实原生 DLL 通信。
- x64 打包／非打包调试配置构建、VerifyLaunchSettings 校验通过；用户确认非打包可运行。
- 实际非打包 WinUI 进程通过静默启动、重复实例恢复和占位文件不变检查。
- x64／ARM64 Release MSIX 已生成并签名；包内容、自包含运行时、PE 架构和签名验证通过。
- 发现的非法帧异常、Verb Id、BOM 和启动配置问题已修复并复测；修正分数缩放图标取整，并验证全部 MSIX 缩放 PNG 尺寸。

### 待验收

完整 GUI／Explorer、登录启动和托盘系统行为，以及最终包安装升级／卸载、ARM64 实机仍待执行。详见 docs/Validation-0.0.1.md；不得将交叉构建或用户的一项运行确认当作这些项目全部通过。
