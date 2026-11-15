# 0.0.23.2 验收记录

## 定位与实现

生产录屏约8.384秒的原帧证据在artifacts/validation/0.0.23.2-refresh-triage/。0.655／1.441秒右上文字消失，1.310秒访问键提示短暂出现；详情保持打开、未调整窗口。本版未以本机日志替代生产证据。

直接源码链为PublishSnapshot无条件HasIssueFilter通知→TasksPage.Invalidate→协调器清集合、临时还原主区、显隐筛选并强制布局。任务行原本已按ID复用，快照已有250ms合并，故分别修通知与共享排版，不改业务调度和刷新频率。

任务页按真实值发布范围／标题及命令可用性。协调器复用测量，命令标签变化只调整文字测量；相同输入和等价宽度不转移命令、不关菜单。必要变化保持先关层、后异步最小增删。尺寸适配只写变化的本地资源，分隔符校准缓存；33 DIP参照高度、20 DIP主图标／16 DIP菜单图标及展开黑边修正保持。详情不逐次ChangeView恢复相同位置，原尝试对象、折叠和23.1拉伸保持。

## 直接结果

证据：artifacts/validation/00232-work/。

| 检查 | 结果 |
|---|---|
| 最小x64 Debug非打包 | 成功，minimal-debug-build.log |
| Debug／Release组合 | 各133/133，debug-regression-build.log、release-x64-package-regression.log、对应TRX；新增真实仓库快照通知测试及原行复用、异步快照、历史、概览、详情和导航保护 |
| 实际持续刷新对照 | baseline-refresh.json为23.1原实现，32次采样中494次命令集合变化、38次范围通知、菜单被关；after-refresh-initial.json及最终两配置final-jitter-*.json为23.2，0集合变化、0范围通知、0新增重测，菜单不关、选中ID保持 |
| 交互适配三组 | 两配置各3组：持续进度及12次重复Invalidate仍幂等；真实详情持续刷新保留折叠及Keyboard焦点；等价1 DIP变化不关菜单，真实TaskFocus筛选安全关闭重排，完成后取消禁用及清完成可用 |
| 工具栏矩阵 | Debug／Release各252组，final-toolbar-*.json。三语言、明暗、两密度、600／1000／1900宽，主条高度、背景、OpenBorder为0、20／16图标、设置独立按钮及有序靠右换行通过 |
| x64／ARM64签名包 | 自包含MSIX签名、验签和内容检查成功；三个模块23.2，delivery.json及签名／包日志 |
| ARM64测试交叉编译 | 成功，0警告0错误；arm64-tests-cross-build.log。未运行ARM64代码 |

探针创建独立索引与内存配置，注入上传状态／字节但不执行传输、不连接远端或注册挂载；实际任务页及详情使用生成的XAML／PRI，记录命令集合和控件坐标，并非只匹配源码。基线与结果不合并计数。辅助手写探针曾有nullable反射解箱编译问题，修正后执行；不归为产品故障。

Ext／Stock对52cda0f零源码／工程差异；产品身份、schema8、hmxbk、公共IPC与外部配置保持。MUI覆盖警告保留。

## 待测

生产端相同刷新录屏复验、物理Alt访问键及完整鼠标／键盘操作、读屏、高对比度、跨屏DPI／文字缩放、Windows11 21H2及ARM64实机。程序化Keyboard焦点与集合采样不能代替所有真实输入验收。不推送、不安装生产包、不操作正式挂载、不重启Explorer。23.3再作样式统一，不在本版提前混入。
