# 0.0.9.25 导航改造验收

执行日期：2026-11-06。代码、程序集、清单及产物版本均为0.0.9.25，保留三个工程。代码完成、自动验证与未执行的交互验收分别记录。

## 代码完成

- 移除ApplicationRoutes及NavigationGateway，新增五值ApplicationPage。应用在窗口创建前持有唯一NavigationService，窗口一次注册Frame与页面类型；无窗口、无效枚举、重入或停止状态不发起导航。
- WinUI适配器实际使用Frame.Navigate及EntranceNavigationTransitionInfo，返回使用Frame.GoBack；历史完全由Frame拥有。重复当前页不增加历史，返回后新导航自然清理前进栈。
- 侧栏通过ItemInvoked请求导航，成功事件同步当前页、侧栏和返回状态。标题栏返回始终显示、无历史禁用；Alt＋左方向键使用相同返回入口。ContentDialog等弹层打开时阻止底层返回。
- 五页Required缓存、原有OnNavigatedTo／From、草稿、选择及滚动恢复保留；不增加另一套页面生命周期。真正退出时先拒绝导航，再统一停止并释放页面；保留迟到页面、重复释放、异步清理和错误聚合。隐藏窗口不清历史。
- 任务筛选由TaskFocusService独立保存，页面导航不携带筛选参数；IPC v2、挂载配置、凭据、SQLite及协议行为未改动。实际代码和注释不新增参考仓库路径或来源说明。

## 实际自动验证

证据位于artifacts/validation/，不将历史结果或静态检查记作本版运行测试。

| 项目 | 实际结果 | 证据文件 |
| --- | --- | --- |
| x64 Debug最小WinUI／原生编译 | 通过 | 00925-minimal.log |
| 导航／页面状态／上传问题定位 | 46/46，失败0、跳过0 | 00925-targeted.log／trx |
| x64 Debug全量 | 1029/1029，失败0、跳过0 | 00925-debug-full.log、00925-debug.trx |
| x64 Release全量 | 1029/1029，失败0、跳过0 | 00925-release-full.log、00925-release.trx |
| x64／ARM64签名MSIX | 签名与内容核验通过；版本均0.0.9.25 | 00925-release-full.log、00925-arm64-package.log |
| ARM64测试 | 交叉编译通过，未实机执行 | 00925-arm64-tests-build.log |
| x64非打包Release | 构建通过；由隔离宿主运行实际产品窗口 | 00925-unpackaged.log、00925-winui-result.json |
| 实际Compile登记 | 主185、测试247，无重复；新枚举各一次，旧路由和Gateway不再编译 | 00925-managed-compile-items.json、00925-tests-compile-items.json、00925-evidence.json |
| 源码边界 | 生产无旧URI导航，修改代码无参考来源字样 | 00925-evidence.json |

导航测试使用可控Frame边界验证重复当前页、原生历史委托、返回后新导航、失败重试、导航与返回重入、订阅者异常、停止与迟到页面、重复释放及完整唯一映射。原URI拒绝用例由非法枚举拒绝用例替换，因此总数与上一版1033不同。

## 隔离实际WinUI运行时

一次性宿主位于忽略目录，仅用于本轮验证，不新增工程或产品运行模式。它运行实际非打包App资源、MainWindow和五个Page；绕过产品启动恢复，注入内存外观／定义／启动服务和独立SQLite，不启动正式单实例、管道、托盘或挂载，也不安装新包。

- 62项运行时断言通过，错误0，宿主正常退出码0。证据为00925-winui-result.json、00925-winui-exit.json及其中Area内steps.log。
- 已验证初始首页、侧栏枚举、标题栏返回状态、首页VM导航、Home→Mounts→Tasks连续返回、缓存实例相同、前进历史及新导航清理、设置草稿和真实ScrollViewer位置恢复、重复当前页、独立任务定位、真实ContentDialog打开时窗口返回入口不切换底层页、关闭后恢复返回、三轮五页快速导航、隐藏恢复及退出停止全部页面。
- 这是进程内WinUI API断言；不表示真实鼠标、硬件快捷键或动画观感已验收。运行期间没有保存挂载定义或启动连接，正式配置和同步根保持。
- computer-use初始化两次失败，诊断为`windows sandbox failed: helper_unknown_error: setup refresh had errors`，未继续尝试桌面控制。初次dotnet exec宿主在WinUI原生引导阶段以0xC000027B退出，尚未执行导航；补齐SDK apphost、模块初始化和独立资源后上述断言通过。初次失败保留00925-winui-exit-initial.json及对应Area，未据此修改产品代码。临时宿主编译参数及异步收尾接口的修正属于验证工具，不记为产品修复。

## 产物

| 架构 | 签名MSIX路径 | SHA256 |
| --- | --- | --- |
| x64 | artifacts/packages/x64/HybridMount_0.0.9.25_x64_Test/HybridMount_0.0.9.25_x64.msix | 9BB998CC6F911C12A35C2379D9B8FD33A448F29AC1552C4C7610BD7FD35CB10E |
| ARM64 | artifacts/packages/ARM64/HybridMount_0.0.9.25_arm64_Test/HybridMount_0.0.9.25_arm64.msix | B279510253C539E57478E89A595DB741B1ED99A0945DD2CB60F5171C7C8D6567 |

未安装或切换正式运行版本，未重启Explorer，未访问已取消NAS，未操作远端或正式用户文件。

## 待测和发布

- 真实鼠标侧栏与标题栏返回、Alt＋左方向键、弹层内实际键盘操作、视觉切换动画，以及导航失败时侧栏的最终交互表现。
- MSIX安装升级、正式挂载长时间运行、Explorer及ARM64实机。历史Shell与协议待办继续独立保留。
- 完成后按本次明确授权，以“0.0.9.25｜简化Frame导航并加入切换动画与原生返回”独立提交，普通推送到GitHub及Azure master；不强推。提交SHA、双远端读回及最终工作区状态由artifacts/validation/00925-publication.json记录，不以计划文字冒充已推送。
