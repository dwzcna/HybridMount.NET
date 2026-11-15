# 0.0.23.3 验收记录

## 实现与保全

以9ad00ac的23.2最终代码为基线；23.2未改变Style定义，26项清单与23.1一致。本版合并样式与布局适配，保留23.0菜单和图标、23.1首页与概览、23.2刷新稳定性。

| 原入口 | 统一入口 |
|---|---|
| ProtocolSecondaryButtonStyle／SettingsActionButtonStyle | ActionButtonStyle，原生普通Button模板；对齐留在使用处 |
| SettingsActionIconStyle／TextStyle／PanelStyle | ActionIconStyle／ActionTextStyle／ActionPanelStyle |
| SettingsActionPanel／ProtocolActionPanel | UI/ActionPanel，共用12 DIP、有序换行和Left／Center／Right |
| ProtocolExpanderStyle／ProtocolFieldsStyle | StretchExpanderStyle／StretchUserControlStyle，保留不同目标类型 |
| 两份协议列表容器与挂载列表重复属性 | StretchListItemStyle；挂载列表只额外保留底部8 DIP |
| TaskItemStyle／TaskNormalStyle | TaskContainerStyle；错误／冲突继承并保留语义配色 |

删除隐式Button→Subtle覆盖，轻量操作显式引用；主操作图标20、图文8、组间12 DIP共用资源。挂载卡片复用CardBorderStyle并保留16,12内距。卡片行内32 DIP小操作、菜单16 DIP、展示大图标保留各自用途。主题刷别名因作用域保留，任务状态模板留在单页；不同类型不强行合并。原生CommandBar／Popup、密度字典寿命和逐键更新不变。

动态对话框／详情分页共用ActionPanel；两处动态DataTemplate的旧HybridMount.Helpers.UI改为HybridMount.UI，原Display绑定和事件保持。AGENTS补资源维护规则。schema8、注册表根、hmxbk、公共IPC、包身份未变；Ext／Stock源码和工程对52cda0f零差异，NativeShared不存在。

## 实际结果

证据根：artifacts/validation/00233-work/。所有探针只用独立SQLite、内存配置和展示数据，不连接远端、注册根或执行文件业务。

| 检查 | 实际结果／证据 |
|---|---|
| 样式与绑定清单 | 26→22；无完全重复或空转发；375处XAML绑定和事件不变；styles-before/after.json、style-key-map.json、style-audit.json |
| 最小与最终x64 Debug非打包 | 成功；minimal-debug-build-fixed.log、final-minimal-debug-build.log |
| Debug／Release定向 | 各154/154；debug/release-regression.trx及构建日志，涵盖任务刷新、概览、详情、导航、能力、备份、结构和本地化 |
| 工具栏实际控件矩阵 | 两配置各252组；final-toolbar-*.json，三语言／两主题／两密度／600、1000、1900宽，主条背景、OpenBorder、20／16图标、设置按钮独立靠右及有序换行 |
| 弹窗／面板 | 两配置各129组；final-style-*.json，三协议、备份、冲突的真实生成XAML／PRI，三语言／两主题／密度往返／600、1200宽；550外框、90%高度、展开、列表、状态切换、左右／居中、220宽长文字及隐藏项；动态模板两项Display实际呈现 |
| 首页与详情 | 两配置各73组；final-home-*.json，首页断点、通栏、概览、隐藏恢复和详情拉伸／折叠／滚动保持 |
| 刷新稳定性 | 两配置各3组；final-jitter-*.json，持续快照、菜单、Keyboard焦点、详情折叠、真实筛选及终态可用性保持 |
| 最终卡片样式 | 两配置各24组；final-resource-only-*.json，三语言／两主题／两密度／宽窄下真实MountCard共用外观与显式轻量按钮；DropDown保留专用模板 |
| x64／ARM64自包含签名MSIX | 最终重建、签名／验签与包内容检查成功；final-*-package-build.log、signature-*.log、delivery.json。包内主程序／Stock／Ext版本均23.3 |
| ARM64测试交叉编译 | 成功、0警告0错误；arm64-tests-cross-build.log；未执行ARM64代码 |

最终补充卡片资源引用、清除空白及格式整理后已重新完成最小构建与双架构包检查；其他功能没有变化，不重复无变化的业务回归。

## 过程异常与限制

初次最小构建指出两处协议XAML仍引用旧面板命名空间，改为helpers:ActionPanel后通过。首轮Style探针已完成127组后，误把33 DIP普通按钮与32 DIP轻量按钮的1 DIP顶坐标差分为两行；详细坐标为0/81宽及93/77宽，实际间隔12。改按行中心分组后两配置129组通过。卡片补充探针也曾错误要求DropDownButton采用Button样式；限定普通Button后24组通过，未替换原生下拉模板。保留first-style-debug.json、dynamic-only-debug.json和resource-only-triage-debug.json等过程证据，不将探针失败记作产品通过或隐藏。

RenderTargetBitmap离屏图片部分控件缺失；Windows computer-use初始化与重置重试均报node_repl kernel exited unexpectedly／helper_unknown_error。没有注入系统输入，没有取得可用的窗口像素截图；windows-capture-attempt.json记录失败。因此上述“真实WinUI”仅指真实生成控件的状态、视觉树和坐标验证，不等同完整像素／人工交互验收。

仍待：生产端刷新录屏复验、完整物理鼠标／键盘及Alt访问键、系统选择器、辅助技术、高对比度、跨屏DPI／文字缩放、Windows11 21H2和ARM64实机。不推送、不安装生产包、不操作正式挂载、不重启Explorer。
