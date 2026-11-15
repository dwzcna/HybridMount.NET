# HybridMount.NET 开发规范

本轮目录与分层规整适用于HybridMount主程序和HybridMount.Tests，最低 Windows 11 21H2，支持 x64／ARM64。Ext／Stock保留已提交0.0.21.0（52cda0f）的源码和项目布局。用户本次明确要求优先。保留现有改动；默认本地提交，不推送，不操作正式挂载或重启 Explorer。

## 修改与版本

修改前阅读 HybridMount-devplan.md、Changlog.md 和实际代码，核对工作区及暂存区。版本集中于 Directory.Build.props，程序集／MSIX使用四段版本。按版本完成补丁、最小构建、回归和记录；产物、密钥与临时辅助脚本只放忽略目录。

## 目录与依赖

- Views/Pages、Views/Windows、Views/Dialogs：真实页面、窗口、弹窗与视图代码。Controls：复用控件。UI：布局、格式化、绑定、WinUI及托盘适配。
- ViewModels：绑定状态和命令，类型以 ViewModel 结尾；不放协议、数据库或纯DTO。一个主要类型一个文件，同一partial类型集中同目录。
- Models：业务数据、快照、值对象与纯规则。Commons保留 HybridMount.Commons 根，放服务边界与接口；不另建 Abstractions 或转发命名空间。两层不得依赖 Services、Infrastructure、UI或WinUI。
- Services：连接、同步、任务等编排；仓库、日志、设置、协议和调度边界使用 Commons 契约。具体本地操作适配器归 Infrastructure，不在业务服务复制底层实现。
- Infrastructure：协议、SQLite、文件、凭据、Windows／CFAPI、IPC和原生进程的具体实现；不得反向引用具体Services、UI或Composition。基础设施内部的存储／协议实现接口可同目录保留。
- Composition：显式装配与生命周期接线，不引入DI框架。App／Program作为应用入口保留，Composition/Presentation仅装配无UI对象的纯展示器。
- Helpers：只依赖基础库的通用路径、流及线程工具；依赖业务优先级或Dispatcher的适配器移到实际职责层。
- Ext／Stock：保持0.0.21.0的原生布局及已有共享引用，不新增NativeShared、不重排两个原生工程。结构检查只核验实际原有源码登记、路径及版权，不对原生目录提出新布局要求。

命名空间随目录同步。调整目录必须同步Compile／Page／XAML x:Class、资源、RC／include、def和脚本。跨层global using不得恢复。

明确例外：现有模型的派生展示getter仅转发Commons展示契约，保留已有JSON字段；新展示逻辑归UI。注册恢复快照仅复用BCL RegistryHive／RegistryValueKind枚举的原数字编码，不访问注册表。集合DTO可同文件。测试MockFileSource保留生产模拟源命名空间以替换真实适配器。生成代码和第三方源码不按自有类型命名规则重排。

## 生命周期与安全边界

自有C#／C++为职责、关键参数、线程、失败和释放顺序补充必要中文注释。保持事务、锁、句柄、取消和退出顺序；取消不代表写入回滚。能力结论以实际证据为准。不得改动外部配置根、SQLite schema、.hmxbk、公共IPC、包身份或COM／ABI，除非用户另行授权。

## 测试与交付

保留一个测试工程，按Unit、Integration、Native、Architecture、Fixtures、Acceptance、Assets分类。共享协议服务集中Fixtures，每用例仍有独立状态和清理；保留竞态、取消、恢复和数据保护覆盖。删并旧测试逐项记录替代，不按比例删测，不用实现文字断言代替行为。

使用global.json固定SDK和仓库build脚本，复用增量缓存，同输出目录串行。先最小x64及定向验证，再必要Debug／Release完整回归、ARM64交叉构建、签名包检查。托管结构检查与原生原有路径核对已接入构建，不绕过。真实服务、长时和性能验收显式分组运行；交叉编译不代表实机通过。

验证证据存artifacts/validation/。README只描述当前功能和入口；变更、失败修正和实际结果写Changlog与验收文档。devplan分别维护代码已完成／未完成、已测试／待测试，不引用旧结果宣称当前通过。

暂存只包含本版审阅清单，不撤销原有暂存或用户修改；验证、记录后用“版本号｜中文摘要”本地提交。未执行的WinUI／Explorer、DPI、高对比度、21H2及ARM64设备测试明确列待测。

## 界面资源维护

新增界面先复用Resources/SharedStyles.xaml的现有语义样式。新增Style必须有明确的复用用途；同类外观的尺寸、间距、圆角及图标在同一资源维护，不以页面／协议命名重复定义，不保留空转发别名。布局位置、绑定与事件留在使用处；轻量按钮显式SubtleButtonStyle，普通操作使用ActionButtonStyle，不恢复全局透明Button覆盖。

跨类型只共享颜色与尺寸，必要的类型专用样式及单页状态模板保留并说明例外。C#动态界面引用同一资源，不另造控件工厂或复制外观。密度保持有类型资源及应用寿命，逐键更新；主题使用ThemeResource并核对作用域。命名使用UI／UserInterface。
