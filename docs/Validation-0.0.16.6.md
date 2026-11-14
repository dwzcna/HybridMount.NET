# 0.0.16.6 验收记录

基线0ef5823。版本化补丁、TRX和阶段日志保存在artifacts/validation/00166-work/。本地提交，不推送。

## 实现

- 物化存在性改为SQL EXISTS，缓存统计只加载物化、未删除、非目录候选；上传快照和提交、冲突、目录观察及排除改名入口改用ID、精确源路径或同路径查询。
- 上传映射同一查询内优先原ID，原墓碑仍可匹配；否则选择同源及本地路径的活动项。源路径和本地映射继续执行原字符串精确比较，不合并S3大小写变体。完整Entries快照显式保持本地路径及插入顺序，首项选择不再受新增索引影响。
- 祖先屏障只查询当前路径和祖先，超过512项分批，继续使用原SourceContains和RemotePresence规则；每次提交前仍重新读取。
- 未结束上传、未解决问题及同路径日志由SQL过滤；问题计数／分组不反序列化终态记录。快照原始JSON读取后在仓库锁外解析，完整依赖、取消和恢复快照继续保留全部必要状态。Snapshot的读库与改名日志读取移出ConnectionSync。
- 增加entries_materialized、uploads_local_path和uploads_attention索引；现有恢复问题事务复用原连接。格式仍为7，公共IPC、配置及.hmxbk不变。
- 改名文档按数组身份缓存不可修改视图及ID索引，写入替换数组，旧视图不会被后续修改；损坏仍报告并维持写入屏障。旧源范围读取一次查询ID、版本及blob，关闭数据库后读取正文，保留GUID、取消和版本保护，不持久缓存内容句柄。

## 实际验证

最终x64 Debug／Release各125/125，0失败／0跳过：debug-final.trx、release.trx。范围为RepositoryHotPathTests、LegacyRecoveryRangeTests、ExcludedRenameJournalTests、UploadCommitRecoveryTests、UploadAttentionTests（排除会注册根的ClearedFailedHistoryStillHasIssueAndRestartedRetryKeepsIt）、FileOperationRecoveryQueryTests、SqsEventTests、ExplorerEntryRecoveryTests、SettingsResetPreflightTests和SettingsResetScopeTests。

新增回归覆盖重复源映射／原ID与墓碑优先、大小写和重命名本地路径、600层祖先、跨挂载过滤、问题重试保留、无关JSON不反序列化、SQL索引命中、视图不复制／不能修改／旧读者稳定、损坏日志拒绝，以及旧源范围、EOF、身份／版本／blob篡改和取消。所有数据库、日志和内容文件使用独立随机临时目录。SQS夹具仅访问本机HTTP；清理时会向隔离原生宿主查询普通目录属性，不注册同步根、不连接正式云端。

- 最终x64 Debug非打包：build-x64-debug-final.log；初次最小编译也通过。
- 双架构Release自包含签名MSIX及包检查：package-x64-release.log、package-arm64-release.log。使用既有CN=dwzcn证书，未安装包或修改信任。
- ARM64测试程序集交叉编译0警告／0错误：tests-arm64-build.log；未在ARM64执行。
- 本地化1160键×3语言／8原生字符串×3；git diff --check通过。

## 测量与连接策略

Release暖态每组100样本（预热5次），单位毫秒。原始记录为metrics-release/queries-1000.json、queries-10000.json；Debug测量另存metrics-debug。比较同一独立库中原全表API和新点查，未运行实际Explorer或完整文件传输。测量期间同机仍有构建活动，不能作为空闲设备基准或端到端响应承诺。

| 条目数 | 全表取单行P95 | 点查P95 | 新建连接、注册比较器及SELECT 1 P95 | 已开连接SELECT 1 P95 |
| --- | --- | --- | --- | --- |
| 1,000 | 3.842 | 0.7368 | 0.8693 | 0.0047 |
| 10,000 | 41.8882 | 1.6093 | 1.3045 | 0.0047 |

查询计划分别命中entries_materialized、uploads_attention和uploads_local_path，主键读取由既有主键索引完成。新建连接确有可省开销，但本版优先消除全表与JSON物化，保留短连接生命周期，在现有恢复事务内复用连接；不引入进程级长驻连接，不改变WAL或忙等待参数。本机Microsoft.Data.Sqlite 10.0.12 XML文档说明CommandTimeout默认30秒，用于等待表锁；实测DefaultTimeout也为30。不能以未设置PRAGMA busy_timeout推断原实现完全没有等待。

## 阶段失败

首轮debug.trx实际117通过／8失败：5项为SQS夹具退出查询遇到隔离原生宿主仍是0.0.16.5；2项同步基准误用了仅支持异步测试的xUnit Timeout；1项暴露精确源路径点查按ID排序改变了重复映射首项。已重建Debug／Release同版隔离宿主、去除同步基准的错误标记，并统一原有本地路径／插入顺序。随后125项在两配置全部通过，最终生产编译与包覆盖这些修正；原始失败不删除、不计为通过。

## 待测

真实WinUI任务页与连接交互、Explorer目录切换／源读取预算、生产协议上传下载及长时并发、Windows11 21H2和ARM64设备。纯SQLite等价与受控恢复测试不能代替真实挂载验收。本轮未注册系统根、未操作正式挂载、未安装包、未重启Explorer。
