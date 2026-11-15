# 0.0.23.0 验收记录

## 实现

主工具栏保留原生模板和既有Segmented等高机制。仅在页面CommandBar本地资源内覆盖主条展开背景和OpenBorder厚度；Popup、焦点和高对比度HighContrastBorder继续使用原生资源。主区及设置按钮20 DIP，溢出菜单16 DIP。设置六操作使用显式独立Button样式及有序换行、逐行靠右的Panel，原事件、可用条件和确认流程保持。

用户截图补充的整圈黑边来自模板CompactOpenUp／Down显示OpenBorder；原默认厚度1。本版局部厚度0，真实控件在三语言、明暗和密度切换后展开时均实际读得0；不是仅以透明背景判定修复。高对比度专用矩形没有覆盖。

## 当前直接证据

证据目录：artifacts/validation/00230-work/。

| 检查 | 结果／证据 |
|---|---|
| 最小x64 Debug非打包 | 通过，toolbar-final-debug-build.log；版本23.0 |
| 定向Debug／Release | 各51/51，debug-regression-build.log、release-x64-package-regression.log；布局策略、导航、三语言、外观偏好、重置预检及作用域隔离 |
| 真实WinUI Debug／Release | 各360组，无探针异常。final-debug-ui.json、final-release-ui.json；三语言×明暗×标准／紧凑×600／1000／1900宽，五页真实生成XAML及PRI。实际测量展开前后背景、主条／Segmented高度、OpenBorder厚度、主区／菜单图标、设置每行右边界、独立样式、12 DIP间距及长译文；程序化遍历普通／悬停／按下／禁用状态 |
| x64／ARM64 Release | 自包含MSIX签名、验签及内容检查通过，release-x64-package-regression.log、release-arm64-package.log。证书沿用本机测试证书，未安装产品包 |
| ARM64测试交叉编译 | 通过，0警告0错误；arm64-tests-cross-build.log。未执行ARM64代码 |
| 源码边界 | Ext／Stock对52cda0f零差异，NativeShared不存在；schema8、公共IPC v2、hmxbk和产品身份保持 |

修改前22.1真实WinUI20组位于baseline-debug.json，主条展开由透明变为AcrylicBrush，主区图标16；不能当作本版验证。最初背景修正的8组位于after-debug-initial.json；完整矩阵随后发现宽窗主分隔符保持48 DIP撑高主条，已限定修改分隔符Compact状态的Height，Overflow水平线保持。初轮失败与诊断文件保留；最终两配置360组重新验证通过。

截图final-debug-ui-Tasks.png、final-debug-ui-Settings.png及Release对应图由真实控件渲染产生。探针使用独立SQLite、内存配置和空挂载；不读取正式挂载配置，不调用清空／移除实际入口。原按钮事件和禁用绑定差异审阅，破坏性预检由隔离测试覆盖。

## 待测试

实际鼠标和键盘打开／关闭菜单、焦点全流程、设置确认全流程；高对比度、跨屏DPI和文字缩放、Windows11 21H2及ARM64设备。程序化状态检查不能替代真实输入验收。保留MUI覆盖警告；工具链及包检查的实际输出原样保存，不将所有构建概括为零警告。不推送、不操作正式挂载、不重启Explorer。
