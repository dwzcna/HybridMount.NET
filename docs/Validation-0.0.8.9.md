# HybridMount 0.0.8.9 验收记录

2026-11-04，Windows x64，.NET 10.0.401／VS 2026／Windows SDK 26100。范围为五页唯一 VM 与实际状态／命令归属，沿用本批 8–14 小时预算；三个工程、数据库 v6、配置语义、协议与原生保护规则保持。

## 已完成

| Page | 唯一 ViewModel 与职责 |
| --- | --- |
| HomePage | HomeViewModel：版本展示；导航入口下一版统一 |
| MountsPage | MountsViewModel：选中 ID、每挂载检测／缩略图草稿、保存校验、连接取消、独立忙碌、任务提交及状态 |
| TasksPage | TasksViewModel：选择、取消／重试、捕获来源的单次副本草稿、详情和清除历史 |
| LogsPage | LogsViewModel：筛选、增量快照、读取合并、清除及对话框防重入 |
| SettingsPage | SettingsViewModel：合并外观页面状态、缓存草稿／校验／回滚、实际启动状态和托盘偏好适配 |

Page 保留布局、视觉选择及滚动适配、文件选择器、剪贴板和对话框展示。UI 派发注入 IUiDispatcher，测试不需要 WinUI 控件。App 不创建页面 VM；关闭行为由 ApplicationPreferencesService 提供。退出先停页面通知，再等待已发出的启动／缓存操作，最后释放日志；单页失败继续释放其他页面。离开缓存页面不释放 VM，不停止应用挂载。

## 已测试

| 范围 | 实际结果与证据（artifacts/validation） |
| --- | --- |
| 最小非打包 | 0089-minimal-a／b／final.log，x64 Debug 编译成功；最终全量继续编译全部 XAML |
| 针对性 | 最初 33/33；新增状态回归后 45/45，最终 46/46；0089-state-initial.trx、0089-targeted.trx、0089-targeted-final.trx／.log |
| 完整 x64 | 最终 Debug／Release 各 429/429、0 失败／跳过；0089-contracts-x64-Debug.trx／Release.trx，0089-debug-final.log／release-final.log |
| 签名与交叉编译 | x64／ARM64 0.0.8.9 签名 MSIX 的版本／PE／清单／资源／许可和自包含检查通过；0089-package-x64-final.log／ARM64-final.log。ARM64 测试仅交叉编译，0089-tests-arm64-final.log |
| 结构 | 五个唯一页面 VM，无 UI／App／其他页面 VM 依赖；全部 185 Compile 无重复，五个 VM 各一次；0089-structure-audit.json、0089-compile-items.json |

13 项新增状态回归使用独立本地目录／定义存储及可控 UI 派发：草稿与选择刷新保留、按挂载忙碌、防重复对话框、保存失败恢复原值且不污染其他挂载、拒绝派发后恢复、释放后忽略已排队通知、副本捕获原任务且只提交一次、数据库错误不越过 UI 命令、启动防重入及退出等待、外观通知防重入与失败回滚、缓存输入与读取失败保护、生命周期偏好独立、日志筛选／增量及解除订阅。所有测试不连接正式服务、不注册正式根，不改正式配置。

## 未完成

本版代码范围已完成。首轮新增测试被 xUnit2031 分析器拒绝（Assert.Single 前 Where），修正为谓词重载后复测；0089-targeted-first-analyzer-failed.log 保留。首轮 Debug／Release 各 428/428 通过后，收尾复查补充任务详情／取消／重试的 SQLite 失败边界及日志增量合并，新增故障恢复断言，再以最终代码完成 46/46 和各 429/429；原首轮全量和打包日志保留为 *-before-command-boundary.*。

产物：artifacts/packages/x64/HybridMount_0.0.8.9_x64_Test/HybridMount_0.0.8.9_x64.msix；ARM64 对应目录。签名证书 3FB1D78271BA8F6EDD3CE163F57D93D13FEB83C0。

## 待测试

- 实际 GUI 的反复页面切换、滚动、选择器／对话框、高 DPI、系统登录启动交互和退出；模拟状态测试不能替代 WinUI 视觉交互。
- Explorer、安装升级、ARM64 实机；正式非打包进程未启动，保留自动连接且未注册的用户挂载现场，非打包构建已通过。
- 本版没有新增真实服务写入；0.0.8.8 坚果云／R2 结果属于前版，真实 RFC 6578／AWS SQS 与历史 TEST-RUNNER-01／SHELL-01 继续待测或待定位。
