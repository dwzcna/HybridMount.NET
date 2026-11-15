# 0.0.20.2 验收记录

## 实现范围

单选导入关闭后转交完整独立新建草稿；保存目录、重复路径或缺失目录均在最终提交前校验，导入选择本身不创建配置／任务。导出保留多选及可选密码，事务接纳后关闭并定位任务，应用拥有后续读取、编码、写入和原子提交；任务只存目标、保护模式及阶段。中断不重放或恢复密码，失败／取消可明确重新选择并导出。公共IPC、SQLite和.hmxbk编码／schema／加密保持。

同时修正密度选择回调同步关闭原生下拉层的重入风险，关闭与发布在独立UI回调，保留生成的Debugger.Break及异常语义。保存文件选择器使用表单／退出取消令牌。三语言资源共954个C# RESX／380个XAML RESW键。

## 实际执行

证据根：`artifacts/validation/00202-work/`。全部协议、定义、凭据和数据库夹具使用隔离路径；没有连接正式挂载、安装生产包或重启Explorer。

- x64 Debug最终非打包编译：`debug-final-unpackaged-build.log`，0警告0错误。
- Debug针对性回归173/173、无失败／跳过：`debug-localized-final-tests.log`、`debug-localized-final.trx`。Release同组合173/173：`release-final-build-test-package.log`、`release-final.trx`。
- 内容覆盖应用接纳先于真实工作结束、表单取消不影响后台、四阶段与完成顺序、提交前取消／目标独占保留旧文件及清临时文件、事务接纳失败回滚、防重入／退出、断开导出、三协议及SQS完整往返、无远端请求和任务无秘密、中断展示及重新输入密码；并覆盖现有备份边界、容量、认证移交、详情及问题任务。
- 真实WinUI主窗口加载隔离数据，Debug／Release各113组密度和布局检查通过：`density-focus-debug.json`、`density-focus-release.json`。三语言、明暗、Pointer／Keyboard焦点96次密度切换，检查关层不在原选择回调、标准Padding／高度回归；任务栏右边界、数字并发保存位置及弹窗草稿保持。控件焦点状态由测试程序设置，不能代替真实鼠标或附加VS调试器验收。
- 最终真实WinUI备份探针双配置各37组：`backup-ui-debug-final.json`、`backup-ui-release-final.json`，`Failures=[]`。含启动检查、18组三语言×三协议×明文／加密导入、错误密码重试、单项也不默认选、缺失目录可继续、完整高级设置和规则／凭据转交、取消后零挂载零任务；另18组保护重试空密码和关闭保护清空密码。
- 随机独立MSIX内重复备份双配置各37组和紧凑冷启动密度双配置各113组，均`Failures=[]`；见`backup-package-*-final.json`与`density-focus-package-*.json`。只注册本次随机测试身份，无Shell／同步根扩展，结束精确卸载，未安装生产包。
- x64／ARM64 Release自包含签名MSIX及包检查：`x64-final-package.log`、`arm64-final-package.log`；验签及SHA256分别保存在本目录。ARM64测试工程交叉编译：`arm64-final-tests-build.log`，0警告0错误，没有运行ARM64测试。

## 过程失败与更正

- 早期最小编译遇临时DLL共享锁，增量重试成功；有MSB3026的一次日志不算零警告。备份ComboBox多条x:Bind置同一行导致XBF WMC0610，正常多行排版后编译成功。
- 探针初始错误密码catch只捕CryptographicException，而现有Codec按约定返回InvalidDataException；修正探针，不改变产品统一密码错误分类。
- 密码重试初始仅延时80ms，在最后一组尚未Loaded就操作，双配置各出现一次检查失败，保留`backup-ui-*-attempt1.json`及原始日志。改为等待真实Loaded再测，最终37组均通过；不把探针过早操作当作产品密码残留。
- 密度包内首轮从紧凑冷启动却再次赋相同SelectedIndex，程序化赋同值不会触发原生选择；首项“下拉未关闭”断言失败。探针改为每次切到当前相反密度，真正覆盖第一步紧凑→标准，最终双配置113组通过；保留`density-focus-package-debug-attempt1.json`，不把测试场景错误归为新产品异常。
- 一次直接dotnet最小编译误用Deployment参数，实际产物为Packaged；随后用仓库实际HybridMountDeployment参数完成最终Unpackaged编译，不将前次记录混称非打包。

## 待人工和设备验收

用户截图停在生成的App.g.i.cs断点，PID31900；新日志PID8896有密度切换Begin／End、没有新的UIUnhandled。已补修确认的回调重入风险并完成隔离尺寸回归，仍不能确认现场异常的唯一根因或宣布VS现场已解决。实际鼠标／键盘、附加VS调试器复验和保存选择器取消／权限交互待测。

完整业务点击、实际服务器、生产规模、高对比度、跨屏DPI、Windows11 21H2及ARM64实机待测。隔离控件、随机包和交叉编译均不替代这些项目。
