# WebDAV 与 S3 实施范围

本轮按 0.0.3 WebDAV、0.0.4 S3 两批实现、验证和提交，最后推送 origin/master。仅保留三个工程。SMB／NFS 无版本排期，保留统一源接口、能力声明及工厂注册扩展。

## 接入边界

- 宿主依赖 IFileSource 与可注入工厂，源负责探测、枚举、元数据、范围读取、条件上传、建目录、移动、删除及取消。凭据单独以 Windows 当前用户 DPAPI 加密保存。
- WebDAV 使用 HttpClient 与禁用 DTD／外部实体的 DAV XML，Depth 1 枚举；Range 校验范围、长度与版本，服务器返回全量时使用整文件临时缓存并声明不支持远端随机读取。
- S3 使用锁定版本的 AWS SDK v4；以 AWS 通用桶及 MinIO 为兼容目标，真实服务仍待验收，处理分页、目录前缀、版本化读取、条件写、分片和非原子移动。冲突或不确定写入不自动重试。
- 映射前收集完整枚举结果，拒绝越界、非法 Windows 名称及大小写／文件目录冲突。远端变化只更新干净占位，本地修改和新建继续保护，不自动上传。
- 默认缓存软上限 10 GiB，可调或关闭；统计占位占用和整文件缓存。自动回收仅处理未固定、未修改、非活动且状态明确的内容。
- 本地、仅远端、待脱水使用 CFAPI 系统状态；部分下载、进行中及保护／冲突单独显示，不替换原文件图标和缩略图。

## 旧版与接口兼容

生产程序移除模拟挂载创建入口和模拟协议实现；测试保留模拟源。旧模拟挂载不自动绑定网络源，通过只读迁移恢复通道下载并移除，或由用户明确选择清空。未完成移除的模式与阶段继续保留。

SMB／NFS 预留协议枚举和工厂入口，不添加可点击的未实现协议。未来实现沿用相同的能力、取消、元数据、凭据和本地 NTFS 根约束。

## 验证与交付

- 协议层：真实 HttpClient／SDK 请求的本地 HTTP 测试，覆盖 XML、认证、范围退化、分页、版本条件、冲突、取消、分片与部分完成。
- 宿主：真实 CFAPI 挂载、网络源水合、远端变化和本地保护、缓存回收、旧配置与移除恢复。
- 发布：x64 自动化、非打包运行、x64／ARM64 签名 MSIX、版本和包检查。真实 AWS／MinIO／WebDAV 服务、GUI／Explorer 和 ARM64 实机分别按实际执行记录。
- Git：开发计划纳入项目版本管理；本机 AGENTS、密钥和产物继续忽略。提交前检查暂存内容，完成后正常推送，不强推。

## 已交付的接口与能力边界

0.0.3 提交为 99074ef；0.0.4 在其后追加 S3。本轮代码及实际验证分别记录于开发计划、变更记录和两份 Validation 文档。

| 接口/能力 | WebDAV | S3 |
| --- | --- | --- |
| 枚举/元数据 | PROPFIND Depth 1/0，完整收集 XML | ListObjectsV2 分页/HEAD，前缀目录/空对象标记 |
| RangeRead | 验证 206/Content-Range/版本；200 或弱版本退化整文件 | 验证 206/长度/ETag/VersionId；不把全量响应当随机读 |
| ConditionalWrite | 首次写入用随机文件核对 PUT/DELETE/MOVE 条件 | 核对 PUT/DELETE；复制和分片完成分别验证 |
| AtomicReplace | 未验证保持 Unknown | 未验证保持 Unknown |
| ChangeNotify / StableIds | Unsupported / Unsupported | Unsupported / Unsupported |
| CaseSensitivity | Unknown，映射时拒绝冲突 | Sensitive，映射时拒绝 Windows 冲突 |
| 上传/建目录/移动/删除 | PUT/MKCOL/MOVE/DELETE；非空目录删除拒绝 | 普通/分片 PUT、标记、复制后删除；目录部分结果 |
| 生命周期/取消 | 源会话释放，HTTP 取消，写入不确定结果单独报告 | SDK 会话释放，已知上传记录恢复，写请求不自动重试 |

写契约已实现，当前界面提供挂载和缓存管理，不自动写回本地变更。未来 SMB/NFS 适配器沿用 IFileSource/IFileSourceFactory；未注册协议明确报错。
