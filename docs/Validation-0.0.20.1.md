# 0.0.20.1 验收记录

2026-11-15；源码、隔离验证、本地提交。不安装生产包、不操作正式挂载、不重启 Explorer、不推送。证据位于 `artifacts/validation/00201-work/`，版本补丁为 `0.0.20.1.patch`。

实现：三协议独立草稿及本次只读认证，SQLite事务接纳任务／真实认证阶段后才关闭；Closed之后清除任务筛选并按稳定ID定位。应用拥有保存、停止旧连接和初始化；任务取消、退出和收尾均由原所有者处理。认证或接纳失败不保存、不注册、不停止旧连接；输入变化包含ABA取消。群晖OTP票据单次移交，凭据与附加凭据不进任务、步骤或日志，实际收尾清除引用。

密度更新合并到UI低优先级、只加载一次资源字典，失败反馈及迟到保护；NumberBox范围1—128，非法值和保存失败保留原值／草稿。任务派生按钮解除固定68DIP，原生图标展示器内仅一个Grid叠加两个FontIcon，保留溢出模板。任务页消除共享1200列上限。

| 实际执行 | 结果与证据 |
| --- | --- |
| x64 Debug 最小及组合 | `debug-final-build-tests.log`、`debug-final-74.trx`，74/74；追加延迟任务定位及最终认证清理69/69，`focus-and-submission-debug.log/.trx` |
| x64 Release | `release-build-tests-package.log`、`release-final.trx`，147/147，0失败／0跳过；含接纳事务回滚、三协议失败、真实签名S3只读认证、群晖OTP、慢请求取消、保存失败、应用所有权、任务定位、退出与页面回归 |
| 真实 WinUI 主窗口 | `actual-ui-debug.json`、`actual-ui-release.json`，两配置各60组且Failures为空。实际MainWindow、SettingsPage、TasksPage和产品XBF／PRI，偏好、定义和日志注入独立内存或工作区目录，不启动正式后台。三语言×双主题共48次实际下拉密度切换，3组NumberBox／保存右边界，9组900／1500／1900窗宽任务栏 |
| 图标和文字 | 独立原生菜单修前固定68宽、文字18宽证据 `density-menu-before.json`；修后同Grid双FontIcon、原生溢出完整文字 `density-menu-debug-final.json`，实际任务页也按文字净宽扣除Margin核验 |
| 右对齐 | 1900窗宽时实际toolbar宽1611，toolbar和actions右边界同为1876（标准页面右边距24）；900／1500同样匹配。保存按钮与卡片内容右边界匹配 |
| 交付 | `release-build-tests-package.log`、`arm64-package.log`：x64／ARM64 Release自包含签名MSIX、验签及包检查通过；`arm64-tests-build.log`：测试交叉编译0警告0错误，没有执行ARM64设备测试 |

密度现场只读诊断来自已有日志：UIUnhandled、ArgumentException／0x80070057、stack=null。原简单控件16次切换未重现；本轮真实主窗口48次、双配置均未崩溃，但这不能证明现场异常的精确原因，生产仍需复验。原Compact资源URI已核实有效。

过程失败如实保留：初期头部页面直接访问MountManager被架构断言拒绝，已改由TasksViewModel判断；接纳事务测试自身的池化连接占用隔离数据库，已改为无池连接。UI探针初期构造／成员名和大小写Tag不符真实API，修正后运行；文字DesiredSize含原生Margin，改测净宽；下拉动画未收尾引发探针状态偏差，改用实际SelectedIndex并等动画收尾。最终实际记录均通过，未放宽业务断言。

待测：完整产品三协议点击／辅助功能、高对比度、跨屏DPI、生产异常复验、真实服务器、Windows11 21H2和ARM64实机；不以隔离HTTP、UI或交叉编译代替。保留用户原TaskListItem排版和后续Plan文件，不混入本版。

## 用户补充后的最终密度修复与复验

用户在.20.1继续报告切换和已保存紧凑后的启动参数异常，非调试运行时则标准尺寸未恢复；因此暂缓提交，前表60组仅作为阶段结果。新增实际尺寸断言后确认：仅改偏好／增删资源，ComboBox的Padding仍为12,5,0,7，尺寸没有重新求值。仅覆盖窗口资源也漏掉独立Popup弹窗。

最终实现：先关闭密度选择弹层，离开SelectionChanged后再保存／发布；应用级稳定字典只采用SDK明确尺寸，不动态接入隐式TextBlock样式，两个SDK实际为Double(0)的TreeView Thickness键恢复正确类型；同一UI回调刷新并还原原主题，主窗口和已打开Popup均重新求取ThemeResource，不重建页面、模板、输入或滚动状态。关闭窗口解除覆盖及订阅。

最终证据：`cold-compact-dialog-debug-final.json`为非打包实际主窗口65组；`density-package-debug-final.json`、`density-package-release-final.json`为真实独立MSIX、双配置各65组且Failures为空。随机包不含Shell扩展或同步根注册，偏好／定义／日志为独立注入，包身份留在对应identity.json；每次只卸载本次精确包，未安装生产包。检查启动紧凑、三语言×明暗共48次切换、任务右边界、保存位置和打开弹窗的四次往返。实际ComboBox带标题整体高度52↔60、Padding 12,1,0,3↔12,5,0,7；弹窗TextBox.MinHeight 24↔32，草稿和选择保持。Release追加断言布局发布不发生在选择回调或打开的下拉层内。

最终源码交付重新执行：`release-density-final-build-tests-package.log`为147/147及x64签名包；`arm64-density-final-package.log`、`arm64-density-final-tests-build.log`为ARM64签名包／检查及测试交叉编译0警告0错误。此前相同名称以外的构建不代替此次结果。

Computer Use的Node helper两次启动失败（helper_unknown_error），没有真实鼠标输入；实际WinUI／MSIX测试由自有隔离程序设置选择索引、等待关闭及动画后测量。标准尺寸恢复已有可重复证据，但现场参数异常缺少原始原生栈，未声称已确定所有触发位置；生产调试及真实鼠标／辅助功能、高对比度、跨屏DPI仍须复验。

## 紧凑切回标准仍中断后的追加修订

用户在首个.20.1提交后报告调试时回切仍中断。没有修改生成代码或关闭Debugger.Break。标准回切不再Clear／Remove资源：在首次挂接前读取全部16个SDK标准尺寸，核对标准／紧凑值类型，保持应用拥有的同一字典及所有键，逐项写回标准值。窗口释放只取消调度，应用资源随Application寿命保留，新窗口复用，不留窗口引用。异常订阅移到App.InitializeComponent之前，在生成断点前记录类型／HRESULT／堆栈，并记录Density.Compact／Standard.Begin／End固定阶段；不改Handled。

追加最终实测：`density-stable-package-debug.json`、`density-stable-package-release.json`均为真实独立MSIX各65组；`density-stable-standard-start-debug.json`为标准冷启动对照65组，Failures均为空，包含严格的Padding、MinHeight往返及草稿保持。全部随机包已按精确身份卸载，未操作生产包。`release-stable-final-build-tests-package.log`为150/150、0失败0跳过及x64签名包；`arm64-stable-final-package.log`和`arm64-stable-final-tests-build.log`为ARM64包／交叉编译0警告0错误。

`interactive-standard-resources.json.resources.json`留存SDK标准值与类型基线，证明16项均可正确读取，两个TreeView键标准为Thickness、紧凑原值却为Double(0)。没有把资源键移除认定为现场异常的唯一根因；隔离测试没有附加Visual Studio调试器，真实鼠标工具亦不可用，用户现场调试回切仍须复验。新增诊断可在再次中断时直接取得先前来不及写出的证据。追加补丁`0.0.20.1-density-followup.patch`独立保留，不改写首个提交历史。
