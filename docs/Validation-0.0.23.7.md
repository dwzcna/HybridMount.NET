# 0.0.23.7｜首页布局验收

证据：artifacts/validation/00237-work/。隔离配置、SQLite和日志，不连接生产挂载。

- 最小x64与Debug／Release定向各104/104（debug-final.trx、release-final.trx）。
- 实际隔离WinUI两配置各73组（home-debug.json、home-release.json）：三语言×明暗×双密度×600/780/1000/1900窗口，共48个首页布局；其中600/1000另24组真实任务详情，及一组隐藏恢复／全部任务导航。反复跨越640 DIP内容断点，断言管理挂载首位、日志其次、设置最后；就绪到入口、入口行间、入口到概览实际间隔均16 DIP。保留全宽任务卡、唯一滚动区、最多三项概览及隐藏期统计。
- 声明／视觉树中三入口顺序与布局一致，TabIndex同值且IsTabStop开启，三个按钮均可程序化获得Keyboard焦点；没有将该结果当完整物理Tab或屏幕阅读器验收。
- x64／ARM64 Release签名MSIX、验签和实际资源／内容检查通过（release-*-package.log），ARM64测试交叉编译0警告0错误（arm64-tests.log）。原生ARM64任务仍有本机ATL/MFC LIB目录警告。
- Ext／Stock源码和项目对52cda0f零差异，版本统一23.7。

探针过程：首次遗漏Input命名空间，随后桌面FocusManager的Next＋FindNextElementOptions不支持，而不带options的桌面重载也不支持，错误保存在home-debug-probe-*-error.json。改为验证真实声明顺序、相同TabIndex和焦点可达；并修正探针对默认TabIndex=0的错误假设。最终两配置73组全部通过，未为纯位置调整增加实现文本断言。

待测：完整物理键盘／辅助技术、像素视觉、高对比度、跨屏DPI／文字缩放、Windows11 21H2、ARM64实机及生产现场。不推送、不安装生产包、不操作正式挂载、不重启Explorer。
