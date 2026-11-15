# 0.0.20.3 验收记录

## 代码与边界

五页共享根使用完整宽度，右侧CommandBar使用独立Border底板及原生模板，与Segmented共享ThemeResource背景和圆角。辅助命令的可见性同步到底板；保留按钮、复合图标、快捷键和溢出语义。主题／密度使宽度重测，重排先异步关闭溢出。

ProtocolCapabilitiesView的全部说明、检测／取消、写入风险、进度、历史清理及能力行放入Expander。固定容器与_rows分离，Show不清固定控件。展开仍只请求已有的本地历史，不启动云请求／检测／清理。

用户新增左右组等高属于后续0.0.20.4，未混入本版；当前高度差由本版原生模板保留。

## 实际测试

证据：`artifacts/validation/00203-work/`。

- 最小x64 Debug非打包编译：`minimum-debug-build.log`，0警告0错误。
- 同版隔离原生映像及针对性Debug／Release各90/90、无失败无跳过：`debug-build-tests.log`、`release-build-tests-package.log`及对应`*-final.trx`。覆盖HeaderLayout、PageViewModel、MainWindow、协议能力、显式检测安全及认证接纳。
- 隔离真实主窗口双配置各186组通过：`layout-ui-debug-final.json`、`layout-ui-release-final.json`。含180组三语言×明暗×两密度×600／1000／1900宽度×五页、两页溢出打开后重排，以及三协议完整折叠／展开。核对实际右边界、宽窗不受1200限制、左右不重叠、底板和Segmented实际颜色／圆角、三类任务筛选、已有命令可用性。
- 协议能力补充三语言×明暗×三协议共18组，双配置各19条（含启动版本）通过：`layout-capabilities-*-final.json`。验证折叠只有标题、展开后取消可达、刷新不丢固定控件／清理路径、展开不触发ProbeRequested／CleanupRequested。
- XAML页面渲染：`tasks-light-600.png`、`tasks-light-1900.png`、`tasks-dark-600.png`、`tasks-dark-1900.png`。已查看最小窗口渲染；这些为RenderTargetBitmap的透明背景页面图，不是完整桌面窗口截图或实际鼠标输入验收。
- x64／ARM64 Release自包含签名MSIX、验签／包检查通过：`release-build-tests-package.log`、`arm64-package.log`，本目录保存两架构SHA256及验签日志。ARM64测试工程交叉编译`arm64-tests-build.log`0警告0错误，未执行ARM64测试。

## 过程失败

首轮临时探针直接调用内部ShowProbe／ShowProbeHistory导致CS1061，改用反射后通过；不修改产品可见性。首轮能力折叠检查仅等待100ms，在原生动画完成前断言高度失败，保留`layout-ui-debug-attempt1.json`；等待原生动画收尾并强制完成布局后，最终双配置及18组语言／主题覆盖通过。未把该计时问题归为产品残留空白。

## 待测试

实际鼠标／键盘、完整命令／选择数据、高对比度、跨屏DPI与系统文字缩放、真实服务和生产规模、Windows11 21H2与ARM64设备待测。隔离注入定义、偏好及数据库，未操作正式挂载、安装生产包或重启Explorer。既有密度VS现场异常唯一根因仍未证实，不能以本版隔离结果替代用户现场复验。
