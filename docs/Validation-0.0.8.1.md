# HybridMount 0.0.8.1 验收记录

2026-11-04，Windows x64，.NET SDK 10.0.401／VS 2026／Windows SDK 26100。三工程、程序集／MSIX 统一 0.0.8.1，IPC v2、SQLite v6 与备份 v3 不变。0.0.8.1／2 合计预算 90–150 分钟，GUI 等待另计。

## 已完成

- 上传及远端更新任务的十进制单位自动换算、两位小数及精确字节提示；文件计算仍基于原始字节，目录按条目。
- 挂载列表／详情及窄窗上下布局；状态按钮、失败重试、连接取消、固定移除模式、长诊断、按挂载忙碌。
- 身份增量刷新、选择／滚动恢复、每挂载检测草稿、刷新不保存、保存防重入及失败恢复。

## 未完成

本版代码范围已实现。GUI 和实机未测不等于交互验收通过；远端容量与任务排序留给 0.0.8.2。

## 已测试

| 范围 | 实际结果／证据（artifacts/validation） |
| --- | --- |
| 最小 x64 WinUI 编译 | 0081-minimal-fixed.log 通过，包含新 XAML 及事件编译 |
| 针对性 | 14/14，0081-presentation.trx／0081-presentation.log；单位边界与 Int64、目录计数、精确字节、连接中取消、空选中／停止／移除限制、集合无 Reset |
| x64 Debug／Release | 各 349/349，0 失败、0 跳过；0081-contracts-x64-Debug.trx／0081-contracts-x64-Release.trx，0081-debug-final.log／0081-release-final.log |
| 签名 MSIX | x64／ARM64 0.0.8.1 自包含、版本／PE／资源／依赖／许可／缩略图门控核验通过；0081-package-x64-final.log／0081-package-ARM64-final.log |

产物：artifacts/packages/x64/HybridMount_0.0.8.1_x64_Test/HybridMount_0.0.8.1_x64.msix；ARM64 对应目录。证书指纹 3FB1D78271BA8F6EDD3CE163F57D93D13FEB83C0。

## 待测试

- Computer Use 初始化及重试均退出，诊断为 windows sandbox helper_unknown_error；未使用其他 UI 自动化。实际按钮／选择／滚动、保存失败恢复、窄窗／高缩放／高对比度仍待测，模型及 XAML 编译不能替代 GUI。
- 本版非打包启动未执行：保留用户新建 D:\myweb（自动连接、未注册）配置及现场，不临时更改正式 AutoConnect；0.0.8 的原根只读核查不算本版启动通过。
- Explorer、安装升级、ARM64 实机和真实 AWS SQS 持续按原边界待测；历史 SHELL-01 不宣称修复。未访问已取消的 NAS、未创建远端测试数据。
