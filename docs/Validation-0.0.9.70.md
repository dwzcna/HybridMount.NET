# 0.0.9.70 页面操作按钮与筛选控件统一

基线2a18e8a（.69），分支codex/native-backend。.69提交后依照子代理计划实施，并纳入用户随后明确的任务页同一行“左侧筛选、右侧操作”规则。原估实现和自动验证1—2工作日；本轮代码与以下自动验证已完成，人工及设备验收另计。

## 实现完成

- 五页页面操作统一为图标加文字。共享CommandBar样式沿用日志页的右侧标签和动态溢出；首页导航、设置启动／缓存操作也在空间不足时进入省略菜单。日志加载更早与挂载断开警告重连保持原独立入口并增加图标。
- 任务页将筛选和动作置于同一行：左侧两个AppBarToggleButton始终保留，宽度不足只折叠文字，不移到省略菜单；右侧动作靠右并动态溢出。依据真实溢出和展开标签宽度合并UI布局，余量足够才恢复文字，临界处保留8DIP余量。普通任务进度不重做筛选测量；卸载／释放拒绝迟到更新，解除新增状态订阅。
- 筛选互斥、双向绑定、导航保持及清除挂载／路径定位的原语义不变；普通toggle再次点击关闭。操作Click、AccessKey、启用状态和清理历史菜单保留。
- 挂载卡片的操作及右侧快捷入口增加文字；窄卡缩小容量列，完整图标文字的实际宽度决定可见前缀。隐藏内容的静态翻译先更新再测量，语言或模型变化只合并一次布局；菜单捕获挂载身份、真实配置回显和缩略图能力限制保持。
- 增加“诊断”短标签，650个资源键×简体中文／英语／香港中文；复用原有其他标签及自动化名称／提示。四工程、HybridMount.exe、NativePreview身份、IPC v2、配置与SQLite格式保持。

## 已测试

证据根目录：artifacts/validation/00970-toolbar/。

| 验证范围 | 实际结果与证据 |
| --- | --- |
| 最小与最终x64 Debug非打包 | 编译通过；minimal-x64-Debug.log、task-alignment-x64-Debug.log、final-x64-Debug.log |
| Debug／Release针对性业务回归 | 各85/85、0失败／0跳过；页面模型、消息关闭／迟到、语言、任务行／卡片／平均速度／队列；verified-*.trx。最终页面布局代码由下列真实UI覆盖；上述无界面业务测试输入未变 |
| 真实WinUI五页 | 三语言×520／800／1280／1700物理窗口宽度；验证可见图标文字、左／右对齐、真实溢出菜单展开后当前语言与可达性，资源无缺键；smoke-final-layout-result.txt |
| 任务筛选 | UIA Toggle验证互斥、再次关闭及导航保持，真实Invoke清除仅挂载／路径定位；窄窗筛选图标保留、右侧动作省略，宽窗可容纳时文字恢复。tasks-narrow.png、tasks-wide.png；英文在页面最大可用宽度仍不足时保留图标符合规则 |
| 挂载卡片 | 420／700／1100控制宽度及2800虚拟宽度，三语言下逐项校验无缺失／重复、无文字裁切、完整展开时省略按钮消失。2800使用1800宿主窗口内的宽控件布局，未冒称超出本机显示器的实际窗口验收；mount-card.png |
| 卡片身份／真实状态 | 实际打开菜单，控件动作适配入口注入核验开关意图及失败后的真实状态回显、重绑定后旧菜单被拒绝、新菜单携带新ID；缩略图不可用时两处入口均隐藏，不执行挂载操作 |
| 进度与消息附加回归 | 千次进度保持同一行、ListViewItem、ProgressBar及选择，无Replace；P95=1.3553ms、P99=2.7774ms。独立InfoBar关闭状态、密度／导航保持、三表单草稿／错误／焦点／HTTP提示关闭再开及重复退出清理通过 |
| 最终UI输入 | ui-input-hashes.json确认隔离测试所用HybridMount.dll与最终Debug产品映像一致；截图来自隔离真实WinUI渲染，非设计稿 |
| x64 Release非打包 | 构建通过；delivery-build-x64-Release.log |
| x64／ARM64 Release签名MSIX | 签名验证、包版本／架构／自包含／声明及实际PRI／MUI检查通过；delivery-package-*.log、signature-*.log。650键×3语言及8原生字符串×3通过；final-packages.json记录SHA256 |
| ARM64测试工程 | Release交叉编译通过，0警告／0错误；tests-ARM64-Release-compile.log。没有ARM64实机运行结果 |

这些为本轮隔离自动验证；界面进度时间不替代Explorer或缩略图COM性能测量。.70未改原生热路径，未重复.69的独立MSIX／千样本COM矩阵。

## 阶段失败与待测试

首轮85项为78通过／7失败，隔离测试目录遗留.69宿主导致严格版本握手失败；更新隔离宿主后双配置各85/85通过。原始targeted-Debug.trx及日志保留。

辅助UI用例先后修正溢出控件Loaded前的标签断言、异步窗口尺寸等待、UIA peer获取及类型编译、将定位清除误当成普通toggle清除的断言，以及FontIcon内部TextBlock引起的单项查找问题。本机窗口被显示器约束到1940宽，超宽覆盖改为虚拟控件布局。各阶段日志保留；这些用例修正不作为产品故障修复声明。最终smoke-final-layout退出0并完成全部收尾。

人工键盘／AccessKey／读屏、系统高对比度及文字缩放、长时间真实数据交互、正式包安装升级及ARM64实机仍待测。浅／深色本轮仅做控件资源存在性自动检查和隔离浅色截图查看，不冒称完整主题人工验收。未推送、未改master、未操作正式挂载、未重启Explorer、不补跑.33停止专项。


## 同版维护：移除自动构建中的PowerShell（2026-11-11）

本维护发生在上述工具栏版本提交和验证之后。用户明确要求不升版、不提交、不构建、不打包；当前版本保持0.0.9.70，未暂存或提交。此前构建、测试和签名包结果不覆盖这些维护改动。

- **实现完成**：资源契约校验改为ValidateResourcesTask.cs及MSBuild目标；清单三档副本改为PrepareManifestTask.cs；三语言原生MUI由工程直接调用所选SDK的rc.exe及VC链接器，保留x64／ARM64输出、仅数据模块和增量输入／输出。对应Test-Localization.ps1、Prepare-Manifest.ps1、Build-NativeMUI.ps1删除。手动Build.ps1／Test-Package.ps1保留，后者复用独立资源校验目标及已有PRI转储。
- **用户报告的中间失败**：17:31实际VS构建报告MSB3755及随后任务未注册的MSB4036；RoslynCodeTaskFactory不会对Task正文内Reference的Condition执行MSBuild求值，显式System.*名称也不在工厂搜索目录中。已删除两项任务的多余显式引用，使用工厂自带的mscorlib／netstandard及MSBuild引用。17:40实际VS构建报告MUI工具缺失；VC默认props会清空WindowsSdkVerBinPath，改用WindowsSdkDir及已解析的TargetPlatformVersion，并分别报告缺失工具的完整路径。两次失败均保留为实际用户反馈，未记为通过。
- **静态检查**：五个项目／targets／proj XML可解析；两项C#任务仅语法解析无错误；保留的Test-Package.ps1仅语法解析无错误。只读检查本机任务工厂的默认引用及引用程序集元数据，确认JSON序列化、XML、LINQ、SHA-256等所用类型在netstandard中；SDK 10.0.26100.0 x64 rc.exe及VC 14.51.36231 Hostx64链接器文件存在。自动构建文件及活动脚本中无上述已删脚本或PowerShell Exec引用。证据记录位于artifacts/validation/00970-msbuild-tasks/，不产生产品映像。
- **待验证**：迁移后的实际VS x64／ARM64 Debug／Release构建、三语言MUI与三档清单、包内资源检查。按用户要求本轮未启动MSBuild、编译、测试构建或打包，未更新已有产品／包产物，不能据静态检查宣称构建通过。


## 同版维护：宿主日志GUID与无包初始化（2026-11-11）

承接已确认的同版修复计划；预计源码、回归补充及静态检查半天，运行验收另计。本次不升版、不暂存／提交、不构建、不打包，保留此前未提交的构建维护改动。证据根目录：artifacts/validation/00970-host-import-init/。

### 现场分析依据

附件调试输出包含512次JsonException和2次WinRT InvalidOperationException。本地Unpackaged宿主证据目录有256条原生形状的旧记录，Id为无连字符N格式；原默认Guid JSON读取器需要D格式，前次只读API复现已确认32位读取失败、36位成功。导入失败保留文件，后续日志操作再次扫描，形成重复异常。一次WinRT异常由Language bridge及0x80073D54明确定位至无包进程设置PrimaryLanguageOverride；另一次无调用栈，Shell能力中的Package.Current探测存在同类路径，本次按计划增加前置身份判断。17:51生命周期记录已到Ready，未据这些首次抛出记录认定当前进程崩溃。

### 实现完成

- 原生宿主JSON Id改为标准D格式，文件名保持N格式。C#仅对HostDiagnosticStore.Entry.Id使用局部转换器，接受32位N及36位D字符串，写出统一D格式；不改变公共JSON选项、IPC v2、配置或SQLite结构。
- 导入器原有大小、角色、内容Id与文件名核验、按GUID去重、先提交再比较原字节并删除的保护保持；损坏记录保留。未现场迁移或删除旧文件，后续正常启动时由原导入流程处理兼容记录。历史RootIconLookup所代表的图标查询故障不在本次修补范围。
- 语言发布通过原生包身份查询，仅明确有包才调用PrimaryLanguageOverride；包身份／覆盖失败与原生摘要失败分别记录阶段和HRESULT，两侧独立尝试。仍由明确语言的PRI上下文提供文案，不更改线程区域格式。
- Shell能力无包分支直接关闭自有扩展，不访问Package.Current；有包继续读取当前包清单，清单缺失／损坏保留有包状态并关闭能力；身份及包位置未知错误保留原异常，不伪装成无包。仅增加内部可注入辅助入口，无新增公共接口。

### 回归已补充，尚未运行

- HostDiagnosticCompatibilityTests：256条N、D或混合记录、无GUID转换首次异常、按同一GUID跨格式重放及重启去重、清除不复活、标准写出与转换器字段隔离；非法类型／格式、文件名不符、SQLite实际插入失败、提交后删除前内容变化保护。原有损坏JSON等回归保留。
- UnpackagedInitializationTests：有包／无包语言发布、覆盖失败仍发布摘要、身份未知诊断、两侧失败分开；无包不访问包位置、有包读取当前清单、身份／位置异常不当作无包，以及清单缺失／损坏回退。
- 新测试使用临时目录和注入委托，不访问系统真实语言、包注册或正式挂载。测试工程已有生产源码登记及SDK默认测试源包含保持，不新增工程。

### 实际静态检查与限制

六份修改／新增C#源码仅用Roslyn ParseText检查，零语法错误；主工程、测试及原生宿主三份XML可解析。人工核对原生GUID字符串生成与GuidN支持D格式的实现；未调用C++编译器。git diff --check通过。既有12项非文档维护路径的哈希／删除状态保持，暂存区为空，Directory.Build.props未改，仍为0.0.9.70。

现场256条JSON证据的名称和SHA-256在前后快照中完全一致；host-evidence-before.json、host-evidence-after.json及preservation-checks.json保存结果。static-syntax-checks.txt保存实际语法检查输出，各版本化补丁的副本保存于本证据目录。

以上静态检查不等于编译或回归通过。新增用例、实际Debug／Release、真实WinUI有包／无包启动及包内验收均待执行；本轮未启动MSBuild、产品或测试程序，不更新现有二进制或MSIX，不操作正式挂载，不重启Explorer，不推送、不改master。


## 同版追加：任务筛选、即时提醒与当前模式还原（2026-11-11）

承接用户在异常维护后的追加要求，代码与静态核验预计约一个工作日，运行和设备验收另计。范围固定为任务页三段筛选、Subtle按钮、启动与托盘间距、全应用即时提醒外观，以及只清理当前运行模式的设置还原。继续不升版、不暂存／提交、不自行构建、不打包；不执行实际还原、不操作正式挂载、不重启Explorer。

### 已完成代码

- 按只读参考CalendarPage.xaml的CommunityToolkit Segmented实现全部／同步问题／冲突。新增单一索引状态，兼容原IssuesOnly／ConflictsOnly并保持互斥和任务定位；分类变化一次刷新，切回全部不清除已有问题定位。布局及语言变化合并测宽，窄窗仅隐藏标签、保留图标／提示／读屏名；右侧继续使用原生CommandBar溢出菜单。依赖固定CommunityToolkit.WinUI.Controls.Segmented 8.2.251219，无跨仓库引用。
- 页面按钮基于原生SubtleButtonStyle；AppBarButton沿用系统命令模板，仅将状态主题资源映射到Subtle并使用原生圆角／边框资源，保留Icon、Label、Flyout及溢出。启动卡片Spacing从16收至8，使用紧凑设置行，实际启动说明放在对应行，宽布局移除空行间距。
- 按只读参考Views/MainWindow.xaml:48，主窗口右下角统一承载即时InfoBar，Margin24／MaxWidth560，可关闭、可滚动，全部关闭时折叠容器。挂载／任务／日志及设置卡片通过可视生命周期注册独立消息；关闭保留正文和业务状态，用户操作同文可重开，后台刷新／导航不重开已关闭消息，释放后拒绝迟到派发。日志加载更早独立保留。模态表单的错误／探测与证书提示采用共享样式并保留对话框内的可操作性；长期断开状态和确认／冲突选择维持所属业务流程。
- 新增还原卡片。当前模式由原生包身份决定，配置只处理HKCU\Software\HybridMount.NET\NativePreview\<当前模式>，启动项仅处理该模式ProductScope；不按输出目录推断模式。无包数据范围为LocalAppData下HybridMount.NET\NativePreview\Unpackaged；有包范围为Packaged物理缓存目录及当前包族的LocalState、LocalCache、RoamingState、TempState。逐个删除所列应用目录本身，保留共享父目录、另一模式、正式版、程序文件和Windows维护的包Settings／SystemAppData等运行状态，不卸载包。
- 预检先检查内存和原始注册表挂载子键、严格WinRT同步根、32／64位注册表及包内／真实Explorer入口，查询故障直接阻止；任何提供程序的有效根与数据目录重叠也阻止。只读核验移除／孤立目录／排除改名恢复记录，未完成、旧记录未验证、损坏及临时意图均保留并阻止还原。完整目录与HKCU路径由确认框展示，默认取消。
- 确认后在配置锁内再次核验并停止接纳业务；退出流程等待自身原生运行时、页面任务及日志排空，停用语言摘要和生命周期写入，再关闭当前模式启动入口、重验并清理。继续持单实例租约及同步根注册互斥量，清理后不重建日志。目录及祖先以不跟随链接的句柄核验，目录FileId绑定确认快照，逐层非递归删除；文件按固定句柄标记删除并忽略只读位，不修改共享硬链接属性。任何阶段失败不回滚已删内容，最后用InfoBar显示位置、原因及HRESULT，关闭后退出。

### 本次实际检查

- Roslyn仅ParseText检查全部修改／新增C#，XAML、工程与资源XML均可解析；没有Compilation或Emit。资源目录和zh-CN／en-US／zh-HK均671键，无重复、缺失或空值，源代码资源键引用完整；git diff --check通过。构建维护11个未追加修改的路径哈希／删除状态保持；主工程在原资源校验导入基础上仅追加Segmented引用，版本未改、暂存区为空。
- 用户19:53的真实构建输出已完成671键及原生8字符串的三语言校验，但C#编译因第50行反斜杠字符转义失败。该行现改用Path.DirectorySeparatorChar，随后静态语法检查为0错误；没有执行新的整体构建，不能记为编译通过。中间失败输出保留于ui-reset-static-syntax-failed-namespace.txt；最终解析结果位于ui-reset-static-syntax.txt。最初尝试加载SDK Roslyn与PowerShell已加载版本冲突，随后直接复用已加载解析器，未编译辅助程序。
- ui-reset-static-integrity.json保存资源、既有维护、版本及暂存保护结果；各版本化补丁副本仍存证据目录。原宿主日志修补阶段的256条前后相同快照继续保留。追加维护收尾只读检查时同一宿主证据源目录已为0条，host-evidence-after-ui-reset.json及ui-reset-live-evidence-check.json如实保存变化；原因未经核验，本阶段未运行产品导入、测试或清理，因此不把256条整体不变写作追加阶段通过结果。

### 回归源码与待执行

新增SettingsResetScopeTests覆盖当前模式删除、另一模式／正式数据／共享父目录保留、包族路径边界、目录替换、未确认路径、取消、锁定／只读文件、迟到外部链接及卷根重叠。现有UploadIssueIntegrationTests补三段互斥、无效索引、定位保持及释放屏障；PageViewModelTests补独立还原反馈、防重入和释放屏障，原独立消息关闭／重开回归保留。新增生产范围文件已登记到测试工程，所有新测试仅补源码，未编译或运行。

仍待执行实际Debug／Release编译、新回归、WinUI三语言宽窄窗／主题／键盘／读屏、真实有包与无包启动；还原必须在隔离模式配置与测试包中核验挂载／恢复拒绝、跨模式保护、链接／身份／锁定／只读、宿主退出／日志停写、策略禁止及清理失败后重试。符号链接用例需要系统允许创建测试链接。当前包的OS数据目录访问及MSIX虚拟注册表清理、安装升级和ARM64实机均未运行验收；历史.70成功构建／包检查不覆盖此次改动。
