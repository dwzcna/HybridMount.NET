# 0.0.18.7 验证记录

基于9f4157e。源码及布局回归预计0.5—1工作日，人工及设备另计。四工程、现有身份、公共IPC、数据库和.hmxbk保持。证据artifacts/validation/00187-work/。

## 实现与来源

已读取用户33c95c17、242d1ed9截图。SDK 2.3.9的原生Expander模板将HorizontalContentAlignment传给ContentPresenter；现有代码只设置外框HorizontalAlignment。局部ProtocolExpanderStyle补齐内容拉伸和16DIP内距，ProtocolFieldsStyle将其传入各字段容器；没有替换原生模板或套全局样式。

规则列改Auto／*／Auto，方式保留合理最小宽度，正文取剩余空间，删除仍紧凑。ProtocolActionPanel按可见按钮的内容测量、空间不足时顺序换行，不改事件、文字或可见性。容量单位自然宽度、开关正常尺寸，保留SettingRow窄窗行为。弹窗仍550宽。

## 实际验证

- 最小x64 Debug非打包及后续Debug／Release针对性各100/100，0失败／0跳过；包含能力、显式检测、配置及排除规则既有回归，见debug-regression.trx、release-regression.trx。
- 独立WinUI探针读取本轮实际三协议表单XAML、真实共享样式、SettingRow及按钮换行面板。只为隔离渲染去掉业务事件和编译绑定、注入内存偏好及本地化文字，不访问正式配置、挂载或远端；不能据此宣称完整产品保存／点击已通过。
- 探针首轮因未注册动态加载的自定义类型而失败，补充编译元数据注册；另一次在窗口缩放布局尚未收敛时取到旧尺寸，改为等尺寸收敛再测量，几何约束不放宽，不作为性能数据。
- zh-CN初次72组通过：三协议、明暗、新建／编辑、0／4／20条长规则、两种窗口；高级字段宽466、嵌套SQS及密钥宽432，弹窗550，最小窗口高度432。最终Debug三语言各72组、合计216组通过，Release英文72组通过，另在受限宽度实际验证操作按钮换行；见layout-*-debug.json和layout-en-US-release.json。两配置渲染截图保存在同目录，含顶部长规则和滚动到底部的嵌套SQS。

## 待测

完整产品新建／编辑保存、Tab导航、错误定位和展开滚动交互，高对比度、100／150／200%实际跨屏DPI，Windows11 21H2及ARM64实机。未安装包、未操作正式挂载、不重启Explorer。

## 最终交付

双架构Release自包含签名MSIX、验签和包检查通过，见package-x64.log、package-arm64.log；ARM64测试交叉编译0警告／0错误，见tests-arm64-build.log，未执行ARM64测试。包SHA256见packages.sha256.json，版本补丁0.0.18.7.patch。验证记录后独立本地提交，不推送，包未安装。
