# 0.0.23.6｜弹窗按钮主题验收

证据根：artifacts/validation/00236-work/。独立配置／SQLite，无生产连接或安装。

运行时基线（23.5，before-debug.json）：WebDAV及S3普通按钮解析为不同Style、有效CornerRadius=0；原生默认按钮使用应用AccentButtonStyle、圆角4。覆盖主题资源为9时，原默认按钮跟随9、普通按钮仍0。该证据与生产截图现象一致，不能证明生产系统版本是根因。基线探针对群晖CloseButton的名字查找与内部InfoBar重名，故中断；已修为只查原生CommandSpace。

共同DialogLayout对未显式指定的三个ButtonStyle使用应用已合并的WinUI DefaultButtonStyle；原生DefaultButtonStates继续切换Accent。调用方显式样式优先，重复显示和关闭可靠清理本入口设置的局部值。CardBorderStyle沿用OverlayCornerRadius；14×14圆形问题徽标是半径7的几何例外。

- 最小x64与Debug／Release定向各100/100（debug-final.trx、release-final.trx），包含交互、页模型、结构与资源。
- 实际隔离WinUI两配置各434项（after-debug-final.json、after-release.json）：三协议及动态弹窗×双密度×明暗×默认/0/9资源×Primary/Close/None×三个按钮共432项，断言普通／默认样式圆角一致；程序化Normal、PointerOver、Pressed、Disabled状态不改形状。另2项验证自定义样式与重复显示释放。
- 两架构Release签名MSIX、验签和实际包内容／资源校验通过（release-*-package.log）；ARM64测试交叉编译0警告0错误（arm64-tests.log）。原生ARM64任务仍有本机ATL/MFC LIB目录缺失警告。
- Ext／Stock源码和项目对52cda0f零差异，版本统一23.6。

未执行：生产机截图复测、实际像素和完整物理输入、高对比度、跨屏DPI、Windows11 21H2及ARM64实机。程序化状态／有效属性不能替代上述验收。探针首次缺少Style强制转换导致编译失败，日志保留；修正后才记入最终结果。不推送、不安装生产包、不操作正式挂载、不重启Explorer。
