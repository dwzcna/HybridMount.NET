# 0.0.18.5 验证记录

基于42e1b81。预计源码及隔离验证约1—2工作日，人工／设备另计。四工程、现有身份、公共IPC、SQLite结构与.hmxbk保持；只读诊断不含认证材料。

证据目录artifacts/validation/00185-work/。

## 实现

显式301／308最多五跳，同源scheme／规范host／有效port及根边界；拒绝userinfo／query／fragment、编码逃逸和循环，Destination独立验证。源资源标签只有本次明确映射才能重绑。方法、正文、条件、锁、Overwrite和范围保持，不改写为GET。

XML有界1MiB；PUT用不可变可寻址快照，不可寻址输入在本地缓存建立DeleteOnClose暂存，借用流不被关闭。每次发送及重放有正文所有者，旧响应关闭并排空后下一跳；整链同一截止／取消和网络失败重试预算。成功响应记录逻辑／最终URI，解析href映回同一次逻辑资源，摘要绑定实际最终表示URI。

## 实际验证及修正

- 首轮测试被xUnit的Assert.Single过滤方式分析器阻止，修正为谓词重载后执行。
- 第二轮70项中67通过。两项为请求配对测试按首个同路径同正文匹配而忽略不同条件；另发现无正文MKCOL在服务端已执行后断连时被底层发送四次。
- 所有修改请求设置明确正文标记后，真实断连仅一次最终MKCOL，返回SourceOutcomeUnknownException。第三轮21项中19通过，剩余两项因异步清理请求交叠，不能按相邻请求配对；改为排空清理后，按方法／逻辑目标／正文摘要的FIFO逐项核对条件。
- 最终Debug71/71、零失败／零跳过，见redirect-debug-final.trx。包含301／308的PROPFIND、OPTIONS、GET／Range、PUT、MKCOL、MOVE、DELETE，精确正文与认证／条件／Destination保留；跨源零访问、未知编码／片段等策略、循环／五跳、不跟随302／303／307、修改后断连单次发送、不可寻址输入磁盘暂存清理／借用所有权、弱摘要＋gzip＋REPORT最终URI映射，以及全链期限和不重置重试预算。
- 最小及最终产品x64 Debug／Release、同版隔离原生及扩大Release结果另附，不以未编译的测试计通过。

## 待验证

真实服务器／系统代理的301／308兼容、生产规模、实际WinUI、Windows11 21H2和ARM64实机。未操作正式挂载、不重启Explorer、不安装生产包。被拒绝的重定向不会带凭据访问目标；已发送修改仍由业务恢复核对，取消不伪称回滚。

## 最终回归与交付

最终 Release 扩大矩阵 352/352，0 失败、0 跳过，见 redirect-release-final.trx 与 build-tests-release.log；包含 WebDAV、RFC6578、上传流程／退避／类型化结果、协议能力及任务调度。最终 x64 Debug 非打包构建成功，见 build-x64-debug-final.log。

x64／ARM64 Release 自包含签名 MSIX、验签和包检查均通过，见 package-x64-release.log、package-arm64-release.log；ARM64 测试交叉编译 0 警告／0 错误，见 tests-arm64-build.log，未执行 ARM64 测试。包 SHA256 记录在 packages.sha256.json，版本补丁 0.0.18.5.patch。按版本独立本地提交，不推送，包未安装。
