# 0.0.9.67 验收记录

基线383d2ba（.66），分支codex/native-backend；保留四工程、HybridMount.exe和当时的预览身份。

## 实现与所有权

新增LocalReadIo，供原生跨进程预览租约与原生本地解码流共用。锁仅保护关闭状态、预算和在途登记；元数据／身份核验、ReadFile及等待均在锁外。每次读独立256KiB以内缓冲、OVERLAPPED、低位事件及shared_ptr句柄所有者；全进程最多16项，单句柄最多一项，避免迟到驱动请求导致无界线程／内存增长。

专用操作线程执行提交并等待真正内核结束，GetOverlappedResult始终FALSE。调用方同时观察取消、源关闭、宿主停止、oplock及期限；请求取消后最多再等250ms，若仍未完成，原操作线程持续保管缓冲和保护引用并占用名额，不能把CancelIoEx返回当作完成。关闭拒绝新读、锁外排空，未完成明确报告ERROR_IO_INCOMPLETE；保护引用析构不持状态锁等待。宿主退出由进程及Job生命周期完成内核对象释放。系统调用本身不合作时不释放仍被内核引用的资源。

关闭终态按租约／挂载／会话保留最近32项，重复调用不重复释放；未确认回收先标记关闭再让出锁，确认不能复活已删除租约。托管读取成功应答后先排空正文帧，再观察取消；释放后的结果不再复制给解码器。既有128MiB／512请求及预览10秒预算保持，独立上传快照／传输预算不变。公共IPC v2和配置／数据库兼容，localReadDiagnostics与thumbnailDiagnostics只读数值统计，不启动生成或读取源。

## 实际验证

- 最小x64 Debug、最终Debug116/116和Release116/116、0跳过。证据final-Debug.trx／final-Release.trx；覆盖既有真实预览、原生解码、脱水留图、缓存身份、上传／恢复、通知及CloudSession。
- 首轮租约10/10；追加容量和停止用例后13/13。实际异步完成计数可见于local-read-Debug.trx；1600ms完成延迟注入时，取消264.4788ms、关闭276.9116ms返回，返回时在途仍为1，完成后为0；不是释放后等待的假取消。
- 16项迟到操作仍占容量，第17项ERROR_BUSY，maximumInFlight／maximumRetained均16，最终inFlight=0、leases=0；证据local-read-boundary-Debug.trx。注入完成延迟不会读取或改写正式挂载，真实读已经完成后刻意延迟完成确认来验证资源生命周期，不冒充真实驱动故障。
- 覆盖80轮取消／正文帧竞态、短读／ERROR_READ_FAULT拒绝部分正文、读取期限、重复关闭、外部PowerShell写打开、在途宿主停止及进程崩溃后句柄可再次打开。已知正文、身份、Pin／InSync／OnDisk保持，受保护阶段不增加源读取。
- 双配置x64非打包、x64／ARM64 Release签名MSIX及包检查、ARM64测试交叉编译通过；日志delivery-build-*、delivery-package-*、tests-ARM64-Release-compile.log及final-packages.json。

## 阶段结果与限制

首次最小编译因内部取消参数误改既有ICloudFilesRuntime签名失败，改为内部核心方法后通过；新增测试首次引用了不存在的BytesRead字段，改为既有Bytes后运行通过。保留minimal-x64-Debug.log和first-local-read-Debug.log。早期Debug116/116之后补入确认／释放迟到屏障，再用最终源码完整复验Debug；因Release原生编译与最后屏障补丁时间接近，另行完整重建隔离原生Release并重跑116项，排除旧输入歧义；最终双配置及交付使用最新源码，不能用早期结果代替最后屏障验收。

真实过滤驱动长时间延迟提交／取消完成、实际持续第三方修改／删除、人工WinUI／Explorer、正式升级和ARM64实机尚未执行。本版未操作正式挂载、重启Explorer或补跑.33停止专项；仅本地提交，不推送、不改master。当前证明限隔离真实API与故障注入，不宣称修复所有系统驱动挂起。

## 产物

D:/src/HybridMount.NET/artifacts/packages/ARM64/HybridMount_0.0.9.67_arm64_Test/HybridMount_0.0.9.67_arm64.msix

SHA256：67E9D9AD6125F93B51BE269D91CD2EF1BB00C64B58AB8ED841DB8855B9B2FBA0

D:/src/HybridMount.NET/artifacts/packages/x64/HybridMount_0.0.9.67_x64_Test/HybridMount_0.0.9.67_x64.msix

SHA256：E37B88EA06EF000CCC6D3FA22EE0C7ECADFD1292F7FE2D338390785DCF5BFAA7
