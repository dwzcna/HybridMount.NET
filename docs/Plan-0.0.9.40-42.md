# 0.0.9.40—0.0.9.42 实施分析

GPT 6.1 Sol按用户要求只读分析，基线为0.0.9.39／0f09d38；分析及审查没有执行测试，不替代后续验收。每版代码、验证及本地提交完成后才开始下一版，不推送，0.0.9.33停止专项保持。

## 0.0.9.40：冲突需重复点击

已证实旧MountManager.Conflicts在CurrentId／PropertyPath占用时立即拒绝，TasksViewModel失败后清空比较，用户需重复核对与选择。问题属性发布还会更新Attention、观察戳和快照戳，整个记录／本地完整戳相等校验可能把本程序元数据写入当作内容变化。

实施采用可取消等待与独占预留，保持Serial→根锁→运行锁顺序，等待释放根锁；新上传、属性发布和改名收尾尊重预留及等待优先权。使用独立异步广播防止丢唤醒和忙循环。只沿CloudUploadIssueProperty保护内核验生成的同路径完整戳链更新比较；仅Attention不单独使正文授权失效，其余状态、代次、快照、回执及事件仍严格。

成功退休已有精确事务实现：MountRepository.Conflicts只退役比较时确认的事件，CommitConflictRemote原子提交索引／基线／终态；上传回执通过CompleteOwnUploadEcho完成同版本回显。没有证据表明应按路径批量清除所有冲突，因此不扩大退休范围。

验证：无关上传／属性占用后一次选择继续、等待取消／退出、重复提交、元数据自写、同大小外部编辑、更新远端版本、相关父子事件、单次条件PUT、远端取舍备份及中断恢复。GUI与生产实际链路另列待测。

## 0.0.9.41：排除与挂起共存

首先隔离复现，不能把程序队列Excluded等同Explorer原生状态。首次远端占位已带MarkInSync；FileExclusionMarkerStore随后设CF_PIN_STATE_EXCLUDED并发布自定义排除属性。既有测试只核对Pin／身份／InSync，未断言原生StorageProviderState、SyncTransferStatus及FilePlaceholderStatus，因此原生“被挂起”来源仍需实证。

逐阶段采样首次枚举、规则标记、队列扫描、按需读取及重连，保存原生三属性、自定义完整向量、Pin／InSync／ModifiedSize／FileId；根据证据修复，不以清图标为由伪造InSync、删除本地内容或移除所有远端匹配文件。保留远端枚举和按需读取语义。

另核查UploadAttentionRules：Excluded原样保留问题，ShellText仍包含其旧失败，可能造成陈旧问题图标。持久失败与未知交易应保留，Shell可见提示单独按排除状态精确收尾；在途／结果未知继续核对，不能冒充成功。

## 0.0.9.42：警告与错误完整回顾

明确遗漏：UploadWorker多数失败只更新任务，RemoteApply只写pending.Error，冲突失败只写恢复事务，排除失败只写ExclusionError；VM还有只显示在对话框的失败，全局异常记录在lifecycle。早期Shell／维护／预览宿主在WinUI与Logger之前运行，不能为了日志创建应用或挂载服务。

此外LoggerService的SQLite保留30天／10000条，但读取只取最新100条，LogsViewModel在该窗口内筛选和导出，导致旧警告／错误虽然持久化仍无法回顾。容量512的TryWrite失败会丢日志，仅LastError记录且后续成功又清除，缺少持续可见的丢失计数。

方案：服务所有者在失败／恢复转换时记录一次安全诊断，关联任务、挂载、阶段、类型和HRESULT／HTTP状态，防止VM重复。级别、搜索、时间和分页下推SQLite；导出明确包含所选范围，并带版本、架构、截断和丢失摘要。队列溢出有可靠计数和可回顾记录，保留异步串行写、清除屏障及退出排空。

早期角色沿用轻量安全诊断文件或写入器，正常应用只读汇集；不引入UI／挂载生命周期。统一清洗URL用户信息、查询凭据、Authorization、签名参数、令牌和原始服务响应，不批量导出注册表、连接配置、完整任务JSON或恢复正文。

测试包括各服务失败可见、100条信息之后仍能筛选旧错误、重启、导出范围、清除不复活、溢出计数、SQLite失败、退出排空及伪造敏感异常清洗。人工反馈、Explorer、真实服务、正式安装和ARM64实机按实际执行，未执行即待测。

## .41实际结论和.42接口落地

.41隔离NTFS证实冷占位Pin=EXCLUDED导致原生等待下载，完整后为9，零字节也取决于PARTIAL。因此改用NativeDeferred保持冷状态和自定义排除图标，读取完整后再设原生EXCLUDED；不隐藏远端匹配项、不为图标水合。dcea3fd已独立提交，实际验收见对应文档。

.42保留Logger旧ReadAsync(token)最新100条契约，新增DiagnosticQuery／DiagnosticPage及完整ExportAsync；查询含固定ThroughId、排他BeforeId、UTC区间和Unicode大小写匹配。UI维护查询代次和清除epoch。异常入口不读原始Message/Data，而取有限结构化字段；同异常弱引用去重，业务状态按有界任务事件键记录。队列丢失和存储失败另计并落摘要，保留退出排空。

早期角色采用GUID安全小记录，不保存参数或异常文案；正常日志工作器按源GUID去重、数据库提交后才删除原字节一致的小记录。GPT 6.1 Sol只读复核发现单个坏JSON会阻断整批；已改成逐文件验证及继续导入，清除在任何未完成证据时失败。此审查未执行测试，不能代替运行结果。完整验收及未执行项见docs/Validation-0.0.9.42.md。

本版最后补齐引擎内部消化后的RFC6578降级警告：按降级时间作为会话事件键，成功转入全量也可回顾，重复检测不刷日志；不从错误文案匹配异常种类，不复制不可信href或令牌。最终实际结果记录于.42验收文档。
