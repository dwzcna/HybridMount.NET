# HybridMount 0.0.8.10 验收记录

2026-11-04，Windows x64，.NET 10.0.401／VS 2026／Windows SDK 26100。范围为 URI／Frame 统一导航、页面缓存和视图恢复，沿用本批 8–14 小时开发验证预算。三个工程、SQLite v6、配置及协议语义保持。

## 已完成

- INavigationService.Navigate(Uri) 严格登记 hybridmount:///home、mounts、tasks、logs、settings。外部地址、未知页、编码别名、参数／片段、相对 URI 被拒绝；不会进入 Frame。页面由生产 WinUiFrame 调用 Frame.Navigate(pageType,parameter) 创建，不再 new Page 或设置 Frame.Content。
- 导航成功事件同步侧栏，防止选择事件反向重入；当前页不重复导航，一级 BackStack／ForwardStack 清空。Frame 缓存五页，每页 Required，VM 创建和真正退出释放协调，页面离开时不中断后台挂载。
- UI 滚动书签只在活动页捕获，隐藏页后台列表刷新不覆盖书签；返回页后在布局阶段恢复，旧导航或停止后的回调失效。选择、草稿继续由各页 VM 保存。
- 退出停止应用导航入口，遍历全部已访问缓存页停止通知；统一异步等待，重复释放共用任务。单页失败不跳过其他页且保持失败状态，等待期间迟到页面也先停止，再释放。

## 已测试

| 范围 | 实际结果与证据（artifacts/validation） |
| --- | --- |
| 最小非打包 | 00810-minimal-a.log／b.log；实际产品含 WinUI Frame 适配器及全部 XAML，x64 Debug 编译成功 |
| 针对性 | 导航及页面状态 36/36；补 SQS 观察边界后 41/41；00810-targeted.trx、00810-targeted-final.trx／.log |
| 完整 x64 | 最终 Debug／Release 各 452/452，0 失败／跳过；00810-contracts-x64-Debug.trx／Release.trx，00810-debug-final.log／release-final.log |
| 签名与交叉编译 | x64／ARM64 0.0.8.10 签名 MSIX 的版本／PE／清单／资源／许可与自包含检查通过；00810-package-x64-final.log／ARM64-final.log。ARM64 测试仅交叉编译，00810-tests-arm64-final.log |
| 结构 | 190 Compile 无重复／遗漏，导航契约／规则／协调器各一次，WinUI 适配器不伪装为无界面执行；00810-structure-audit.json、00810-compile-items.json |

23 个导航用例覆盖五路由、当前页重复、50 次快速切换、缓存实例复用、13 种非法输入、Frame false／异常／失败事件、失败不推进选择、后续重试、侧栏回环及故障订阅、在途请求防重入、全部页面释放、失败后继续及重复释放、异步等待和迟到页面、首页命令经应用入口、停止后拒绝请求。可控 Frame 与源码边界断言独立于产品 WinUI 适配器的编译；不宣称实际 GUI 已执行。

## 未完成

本版范围代码已完成。首轮 x64 Debug 451/452，SqsMountIntegrationTests 在确认文件 Completed 后立即断言汇总 PendingHints，实际为 1；失败保留 00810-debug-first-failed.log、00810-contracts-x64-Debug-first-failed.trx。静态顺序确认 SaveRemote 先提交完成行并通知，DetectRemoteAsync 在 ApplyRemotePendingAsync 返回后再发布汇总，旧测试缺少对第二状态的等待。测试改为同一有界条件等待完成行和最终检测状态，仍校验内容、固定状态、零完整扫描／PUT／POST、断开及单次释放；针对性和最终完整回归通过。未修改协议生产实现，未把本次测试时序修正记为协议功能修复。

产物：artifacts/packages/x64/HybridMount_0.0.8.10_x64_Test/HybridMount_0.0.8.10_x64.msix；ARM64 对应目录。签名证书 3FB1D78271BA8F6EDD3CE163F57D93D13FEB83C0。

## 待测试

- 真实 GUI 的 Frame 路由、快速切换、侧栏、缓存、滚动／选择和退出；实际控件行为、Explorer、安装升级与 ARM64 实机。
- 正式非打包进程未启动，保留自动连接而未注册的用户挂载现场；非打包构建已通过。本版无真实远端写入，不访问已取消 NAS。
- 真实 RFC 6578／AWS SQS、历史 TEST-RUNNER-01／SHELL-01、原 TLS／调试器现场和其他审查待办继续单列。
