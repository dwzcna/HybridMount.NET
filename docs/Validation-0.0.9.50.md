# 0.0.9.50 验收记录

基线ab0eecd（.49），继续codex/native-backend／NativePreview，受检补丁本地整合，不推送或改动master。

## 来源和安全修订

- #29 247df8897371c9321e03c61a8692e29c0446553d：采用显式本地预览所有权确认。open后30秒内未confirm的租约由独立可停止计时器回收，不依赖下一条命令；确认的有效解码由C#任务拥有，直到close／进程停止，不按静默时长误杀。全局名额锁不等待oplock创建或活跃读取，四个名额包含正在打开的请求。
- 保留.45的128MiB／512次预算、完整云状态核验及close最终戳。C#打开后确认，取消／失败归还；通道失效或最终校验失败不能作为图片发布成功，不采用PR原稿吞掉全部关闭异常的做法。
- #36 08d5652068f6ea84855c72578d019f289b5ebbfc：保留既有ReadAvailableThumbnail、本地回退、真实BUSY／CORRUPT HRESULT及60秒安全日志限流。提取缺少的真实SQLite进度超时识别，返回ERROR_TIMEOUT／sqlite-timeout；维持100ms查询预算。既有冷缓存与本地路径通过，没有证据支持放宽500ms，因此不合该期限放宽，也不退回旧的仅缓存入口／统一NOT_FOUND。

内部增加confirmLocalRead，依旧由统一版本握手保护；公共IPC v2、源接口、产品SQLite及缓存schema未变化。Shell依旧不经NamedPipe或CLR取图。

## 实际验证与失败证据

证据artifacts/validation/00950-pr-merge/：

- x64 Debug非打包最小编译通过（minimal-build.log、最终final-minimal-build.log）。
- 当前源码Debug／Release相关各34/34（final-targeted-*.trx）。真实隔离根验证未确认自动回收期间无额外命令、已确认跨过同一期限仍可读取、最终Pin状态变化导致关闭校验失败且名额归还；原生CFAPI、缓存、COM取图、无CLR、仅联机不水合、断开后本地解码、旧限制以上媒体及浏览需求回归保持。
- SQLite递归只读查询真实触发100ms进度截止，明确ERROR_TIMEOUT，源请求不增加，不延长预算。
- 首轮33/34：超时断言之后夹具清理再次查询人工构造的昂贵roots视图，导致用例超时。改为先恢复原表与授权，再走身份核验删除；未放宽产品期限或断言。该轮一个精确故障库及辅助文件先备份，再验证测试GUID／路径／回环源后删除；原生受支持清理2/2完成，根及目录包外核验缺失。见fault-fixture-proof.json、fault-cache-cleanup.json、fixture-cleanup.json及fault-cleanup-proof.json。
- 其他测试注册、缓存按精确所有者／路径／零引用审计收尾，证据00950-test-registry-*、00950-test-cache-*，非目标项保留。

未重新改动包绑定、Explorer激活或自动注册配置；本版重做独立COM和真实原生缓存路径验证，完整系列真实包及构建矩阵在.55。实际Explorer、正式安装和ARM64实机待测，不把交叉编译或早期包结果记为本版实机通过。不访问正式远端，不重启Explorer，不补跑.33停止专项。
