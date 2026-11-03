# HybridMount 0.0.8.8 协议模块边界

两个协议按用户指定路径分别位于 `Models/Mounting/WebDAV` 和 `Models/Mounting/S3`。协议模块包含实际网络、SDK、条件、版本、事件及专用持久化实现；这里的 Models 上级目录遵循本轮约定，不表示全部代码都是无副作用 DTO。

| 模块 | 职责 |
| --- | --- |
| WebDAV/Files | 列举、元数据、Range 和共享快照退化 |
| WebDAV/Transport | DavClient 装配、认证、请求／XML、期限、取消及安全网络诊断 |
| WebDAV/Uploads | 创建／更新／删除／MOVE、条件写、锁与精确回滚 |
| WebDAV/Sync、Capacity、Preview | 每集合 RFC 6578、只读配额、严格有界预览 |
| WebDAV/Persistence、Models | 临时项日志／恢复、原作用域、schema、版本令牌及协议失败 |
| S3/Files、Transport | 分页／HEAD／读取、SDK 配置、SigV4、错误分类与客户端装配 |
| S3/Uploads、Sync、Preview | 条件及分片、回执与移动、UploadId 恢复、稳定完整扫描及事件提示复核、严格预览 |
| S3/Events | SQS 消息解析、队列 SDK、共享消费和按挂载订阅 |
| S3/Persistence、Models | 同事务事件／去重、专用 SQL／schema、队列 DPAPI 与备份扩展、版本及事件模型 |

公共层通过 `IFileSource`、`IRemoteChangeSource`、`IRemoteSnapshotSource`、`IRemoteHintSource` 调用；工厂装配 `IFileSourceProtocol`，会话通过 `IMountSourceRuntime` 绑定日志及分阶段恢复。工厂上下文或探测失败均释放客户端，测试连接不绑定运行日志或触发恢复。页面仍允许选择协议及展示其配置。

公共仓库持有原连接、锁和事务。`IMountProtocolStorage` 只提供模块专用建表、谓词及同事务操作；事件与检查点原子提交、消息落盘后确认的顺序保持。共用部分不再识别 S3 提示、解析 SDK 类型或内嵌专用表名。

`SourcePartialMoveException` 提至 Common，保留原公开全名，WebDAV 不依赖 S3 实现文件。驱动及原公开调用仍使用原命名空间；partial 内私有版本模型的类型和 JSON 字段保持，迁移不重编码旧令牌。配置 DTO 可继续包含 S3／SQS 用户字段，协议条件和凭据模式由装配策略处理。

SQLite 仍为 v6；配置／注册表、备份 v3、凭据保护、游标及远端根／账号／凭据修订作用域保持。结构回归约束指定的全部子目录有实际文件、公共层不含 SDK／报文处理；Compile 清单与旧／新 sqlite_master 的独立比较见验收记录。
