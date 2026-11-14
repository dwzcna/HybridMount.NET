# 0.0.9.62 统一界面与启动修复验收

## 代码实现

基于ec83f36，保留四工程、HybridMount.exe和当时的预览身份。挂载启动连接、容量来源、远端检测、缩略图及SQS开关在后台独立保存，等待期间禁用交互，失败恢复正式值；容量／间隔／连接未提交草稿分别保留。新建MountCreationOptions一次保存后再连接，连接失败仍保留完整选项。配置及备份兼容，公共IPC v2与SQLite表结构不变。

五页统一24／16边距和1200内容列，标准／紧凑布局不重建业务控件；共享SettingRow在窄窗分行、标签关联读屏。三协议内容滚动、固定反馈、字段原位说明和首错焦点；高级区域自动展开，延迟焦点有代次及释放屏障。HTTP警告由地址变化重开。业务明确提供InfoBar严重程度，连接失败返回页面不丢级别，SQS测试共用固定反馈。命令栏自动溢出、筛选窄窗分行、空列表及加载状态、Alt访问键、语义图标／标签／Polite和共享术语入口实现；系统高对比度覆盖标题栏、徽标及错误文字。

## 用户启动失败证据及修复

非打包.62两次StartupFailed记录（会话77b811da4a06404bb06163aabd876220、e969261f49024f7eb861d00626c623d8）一致：COMException，0x80070490，堆栈位于AccessibilitySettings.add_HighContrastChanged→MainWindow构造。该WinRT异常没有可见Message，原弹窗只有“HybridMount.NET 启动失败：”。证据存startup-failure-evidence.json。

改用Windows App SDK面向桌面窗口的ThemeSettings.CreateForWindowId／Changed；监听不可用仅记录警告并让系统决定标题按钮颜色，解绑失败不阻断退出，重复释放幂等。启动错误始终附带安全异常类型及HRESULT。没有通过重试正式挂载来验证。

## 实际验证

证据目录：artifacts/validation/00962-ui/。

| 项目 | 结果 | 证据 |
| --- | --- | --- |
| 最小及最终x64 Debug非打包 | 通过 | startup-fix-x64-Debug.log、final-build-x64-Debug-Unpackaged.log |
| 最终x64 Release非打包 | 通过 | delivery-build-x64-Release-Unpackaged.log |
| 最终Debug针对性 | 135/135，0跳过 | delivery-ui-Debug.trx |
| 最终Release针对性 | 135/135，0跳过 | delivery-ui-Release.trx |
| 真实WinUI隔离验证 | 通过，进程退出0 | winui-delivery-result.txt、smoke/Smoke.cs |
| x64／ARM64签名MSIX及包检查 | 通过，含SettingRow.xbf | delivery-package-x64.log、delivery-package-ARM64.log |
| ARM64测试工程 | 仅交叉编译通过 | tests-ARM64-Release-compile.log |

针对性覆盖新建选项连接前保存／连接失败保留、开关失败回滚／跨挂载隔离／草稿保护、密度保存失败、消息严重程度与关闭、页面生命周期、导航、备份、容量和SQS。测试集合串行，测试内部并发保留；先前38／39项及最终调整前135项记录保留，不取代最终TRX。

隔离验证使用真实产品MainWindow／五页／三协议ContentDialog与编译XBF；派生测试App绕过正式OnLaunched，注入内存配置／启动替身与验证目录SQLite，不启动挂载、托盘、单实例或公开管道，不请求远端。520像素窗命令栏实际宽199像素，挂载／任务／日志分别有3／6／3项溢出。标准／紧凑边距24／16反复切换；已关闭提示跨导航及密度保持；三表单就近错误、首错焦点、高级展开、HTTP关闭后后台Refresh不重开／改地址重开与重复窗口释放均通过。此验证不等于人工键盘／读屏或高对比度主题验收。

## 阶段失败

初期ResourceDictionary属性元素顺序导致WMC0035，批量替换生成了错误的_conflictPublishStatus，以及FormFieldFeedback缺AutomationLiveSetting命名空间，均已修复并经最终构建／测试复验。用户报告的桌面事件订阅故障由日志证实并修复，区别于上述编译问题。

隔离脚本初次lambda把下划线当作参数而非弃元，随后错误假设resources.pri名称，均在辅助目录修正；实际资源为HybridMount.pri及松散XBF。一次HTTP改变后立刻断言没有等待异步TextChanged，加入事件等待后通过，没有据此修改产品。早期日志保留。

## 交付与待测

x64：artifacts/packages/x64/HybridMount_0.0.9.62_x64_Test/HybridMount_0.0.9.62_x64.msix

SHA256：E6E2A353D1F2E2577D0EA8CA37B9EAB4FFF5B6C156D6B7E6DAEF366E5A29DBAF

ARM64：artifacts/packages/ARM64/HybridMount_0.0.9.62_arm64_Test/HybridMount_0.0.9.62_arm64.msix

SHA256：BDA10E53853803DF6E59AF24FADC093B2959A237119E15C8959948D2D5327BBB

真实高对比度切换、主题／缩放目视、键盘及读屏、正式安装升级和ARM64实机待测试。本轮没有安装正式身份交付包、不推送、不改master、不操作正式挂载、不重启Explorer，不补跑.33停止专项。.63—.68按已确认计划逐版继续。
