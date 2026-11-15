# 0.0.19.0 验证记录

基线 `6c7e534`／0.0.18.7；2026-11-15 实施。Directory.Build.props、程序集和包统一 0.0.19.0。实现与验证分别记录，下列结果不代表生产、完整产品操作或设备验收。

| 验证 | 实际结果 | 证据（artifacts/validation/00190-work/） |
| --- | --- | --- |
| 最小构建 | x64 Debug 非打包通过；语言／资源、协议表单和任务菜单均编译 | debug-regression-final-2.log |
| 定向回归 | Debug 128／128；Release 扩大 216／216；加载状态修复后最小产品 Debug 及相关 86／86 复测通过。覆盖语言卫星、区域格式、弱订阅、启动、页面职责、能力、连接／失败恢复、关闭、协议、文件流水线和任务快照 | debug-regression-final.trx、release-regression.trx、debug-final-after-ui-fix.trx |
| 实际 WinUI 与 PRI | Debug／Release 各 36 组通过：三语言 × 三种系统区域 × 明暗 × 600／1100 宽；读取实际产品 PRI、RESX，检查草稿、NumberBox 格式、日期、选择、关闭 InfoBar、滚动及双层字体图标 | ui-probe-debug-final.json、ui-probe-release-final.json |
| 菜单早期探针 | Debug／Release 各 8 组结构检查通过；此探针资源为空桩，不替代上述实际 PRI 验证 | menu-probe-debug.json、menu-probe-release.json |
| 交付 | 双架构 Release 自包含签名 MSIX、验签、包内实际 PE 托管资源／语言卫星、PRI、MUI 检查通过；ARM64 测试交叉编译 0 警告／0 错误 | package-x64.log、package-arm64.log、tests-arm64-build.log |

最终资源为 902 C# 语义键、379 XAML 属性键，各三语言及八条原生文案。初始 880 项迁移核对不是最终资源数量；ExplorerEntrySearchButton.Content 经产品编译发现属于 XAML，放回 RESW；补齐移除全部／缓存保留和明确语义状态键。早期探针 CRLF、框架及 PRI 复制冲突等失败保留原日志，未用失败结果宣称通过。

实际 UI 验证发现弹窗内 IsLoaded=true 而本地 _loaded=false，导致语言事件丢弃。修复后在目标 Dispatcher 核对实际加载状态，再挂接数据及刷新；两配置 36 组重新执行通过。原失败证据 ui-probe-debug-state.json 保留。

批量改名和删除曾被自动审查拒绝；用户随后明确授权，已按精确列表执行。approved-naming-applied.json／approved-obsolete-removed.json 及原文快照记录实际范围。未清理或覆盖原 TaskListItem.cs 的格式改动。

待测：完整产品全部业务交互、高对比度、跨屏 DPI、键盘／辅助技术、Windows 11 21H2、生产服务器和 ARM64 实机。不安装产品包，不操作正式挂载，不重启 Explorer；本地提交，不推送。
