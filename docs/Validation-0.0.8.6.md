# HybridMount 0.0.8.6 验收记录

2026-11-04，Windows x64，.NET SDK 10.0.401／VS 2026／Windows SDK 26100。版本统一 0.0.8.6，三个工程及协议／IPC v2／SQLite v6／备份 v3 不变。预算 30–45 分钟，GUI／实机等待另计。

## 已完成

- 根据用户截图中 0.0.8.3 设置页大窗口左侧空白、内容向右偏移及标题上沿裁切，移除 ScrollViewer 内直接限宽 StackPanel 的默认布局。
- 页头放在独立 Auto 行，下方滚动区占剩余视口；显式水平拉伸、顶部对齐、禁止横向滚动。内部 Grid 占满视口，左起单列最多 1040，宽屏余量保留在右侧，卡片与标题对齐。
- 外观选择和缓存输入使用有上限的弹性列，宽屏保持 300／320 控件尺寸，窄窗口按可用宽度收缩。主题、背景、托盘、启动、缓存偏好及点击绑定保留。

- 首轮全量发现断开竞态：RequestRemoteStop 可使正在读取的手动刷新立刻抛出取消，而 job.Cancel 仍未置位，任务进入普通失败分支。调整为先取消该根的前台任务，再停止共享检测／上传等所有者，保持所有停止信号发出后才等待的规则。

## 已测试

- 设置绑定审计通过：10 项 x:Bind、3 个命名控件、点击回调及 8 个选择项内容／Tag 与 0.0.8.5 完全一致；0086-settings-binding-audit.json／.log。该检查只保证行为绑定保留，不证明视觉渲染。
- x64 Debug 非打包最小编译成功，0086-minimal-final.log；取消与远端更新针对性 41/41，0 失败／跳过，0086-targeted.trx／.log。
- 首轮 Debug 404/405，SettingsArePerMountAndDisconnectStopsBlockedReportWithoutErrors 错误为 Failed，期望 Canceled；保留 0086-contracts-x64-Debug-first-failed.trx／0086-debug-first-failed.log。停止顺序修复后执行上述针对性，完整复测 Debug／Release 各 405/405，0 失败／跳过：0086-contracts-x64-Debug.trx／0086-contracts-x64-Release.trx，0086-debug-final.log／0086-release-final.log。

- x64／ARM64 0.0.8.6 签名 MSIX 的签名、清单／PE／资源／许可／自包含核验通过；0086-package-x64-final.log／0086-package-ARM64-final.log。ARM64 测试工程交叉编译通过、0 警告／错误，0086-tests-arm64-final.log，未在 ARM64 实机执行。
- 产物：artifacts/packages/x64/HybridMount_0.0.8.6_x64_Test/HybridMount_0.0.8.6_x64.msix；ARM64 对应目录。签名证书 3FB1D78271BA8F6EDD3CE163F57D93D13FEB83C0。

## 未完成

本版范围代码已实现；真实窗口和 DPI 验收仍待执行。

## 待测试

- 实际 WinUI 最大化／还原、不断拖动宽高、100%／150%／200% DPI 和跨显示器、展开／收起导航、内容纵向滚动、浅深色及高对比度；标题和卡片无裁切，无横向偏移。
- 安装升级、Explorer 和 ARM64 实机。当前用户新增自动连接且未注册挂载保留现场，本轮不启动正式实例或修改其自动连接设置；没有访问已取消的 NAS。
- 后端回归不加载 WinUI 页面，签名／交叉构建也不冒充 GUI 或 ARM64 实机。TEST-RUNNER-01、SHELL-01、TLS 原现场及其他审查项维持开发计划边界。
