# 0.0.4 验证与交付记录

## 已完成

本节仅记录代码实现：

- WebDAV 与 S3 均已接入真实 CFAPI 挂载、按需读取、手动固定/释放和受控缓存；保留三个工程，SMB/NFS 只保留统一接口和工厂入口。
- S3 的 SigV4、连接配置、SDK 默认凭据/加密密钥、分页、前缀/目录标记、范围版本读取、普通及分片条件上传、复制后删除、目录部分结果与已知 UploadId 恢复已实现。
- 远端完整枚举后再映射，保护 Windows 名称、大小写/类型冲突、本地修改/新建/删除/重命名、固定和活动内容。删除和保护标记不会被旧工作快照清除。
- 旧模拟驱动仅留在测试工程；生产程序只读恢复旧内容以下载并移除，不播种或重新绑定网络根。
- AWS SDK DLL 与 Apache-2.0 License/Notice 随 MSIX 和非打包输出分发。

## 未完成

- 本轮范围无待实现代码；自动双向同步/写回、冲突解决、后台周期重扫属于后续版本。

## 待测试

- 坚果云真实账号/第三方应用密码；CSTCloud 访问密钥、实际签名 region 与桶权限。AWS/MinIO 真实服务兼容。
- 完整 GUI 表单/任务/缓存视觉与交互、Explorer 系统角标和菜单、打包身份的安装升级/卸载、登录启动、ARM64 实机。
- 大目录/长路径/大对象、目录并发变化、网络中断、权限与高压力测试。

## 已测试

2026-10-30，Windows x64：

| 范围 | 实际结果 | 本机证据（artifacts/validation） |
| --- | --- | --- |
| Debug 全量 | 46/46 通过 | 004-all-debug.trx |
| Release 全量 | 46/46 通过 | 004-all-release.trx |
| 真实 HTTP WebDAV | Depth 1、认证、强/弱 ETag、正确/错误范围、200 整文件退化、XXE/越界拒绝、条件写/移动/删除、取消/重定向保护通过 | 同上，WebDavTests/ProtocolRecoveryTests |
| 真实 AWS SDK HTTP | SigV4 HMAC 重算、Path-Style、prefix/分页、版本范围、冲突、普通/分片、取消/恢复、不确定写不重试与部分移动通过 | 同上，S3Tests |
| 真实 CFAPI | WebDAV 水合/远端更新/回收/修改保护、大小写冲突拒绝、S3 水合/保留文件移除、旧模拟只读迁出/源数据库不变与既有清空回归通过 | 同上 |
| x64/ARM64 MSIX | 交叉构建、自包含、0.0.4.0、PE 架构、签名、资源/CloudFiles/启动/菜单、AWS DLL 和 License/Notice 内容通过 | 004-managed-*-Release-Packaged.log、004-signature-*.log、004-package-*.sha256.txt |
| 非打包实际运行 | 静默启动、静默重复启动不抢焦点、正常重复启动恢复窗口、非挂载目标拒绝且文件不变通过 | 004-launch-x64-Unpackaged.txt |
| 生产程序集 | WebDAV/S3/旧版只读恢复类型存在，MockFileSource 不存在；双架构主程序和扩展均 0.0.4.0 | 004-production-types.txt、004-binary-versions.json |
| 真实目标只读探测 | WebDAV 401 / Basic realm=nutstore；S3 401，未返回签名 region，未执行认证读写 | 004-service-probes.txt |
| 桌面工具尝试 | 初始化失败，重置后重试仍为 trusted Node process exited unexpectedly；未执行 GUI 输入 | 004-gui-attempt.txt |

安装包：

- artifacts/packages/x64/HybridMount_0.0.4.0_x64_Test/HybridMount_0.0.4.0_x64.msix
- artifacts/packages/ARM64/HybridMount_0.0.4.0_arm64_Test/HybridMount_0.0.4.0_arm64.msix

已知边界：

- 不宣称原子移动或未验证的原子替换能力；远端非空目录不隐式递归删除，S3 目录移动可部分完成。

- 本地协议 HTTP 服务用于测试真实网络栈，不等于 AWS/MinIO/坚果云/CSTCloud 的认证兼容验收。S3 默认 us-east-1 可修改，当前用户提供的服务尚未确认该值。
- Range、条件写、复制目标条件和分片完成条件分别观察/验证；ChangeNotify/StableIds 不支持，AtomicReplace 保持未知，不能从 ETag 推断 MD5 或稳定身份。
- 兼容端点不支持条件复制时，移动退化为本地临时缓存下载后条件上传；复制成功才删除源，失败保留源或返回逐项部分结果。
- 分片只恢复本地已记录的本程序 UploadId；不扫描/清理其他客户端上传。网络丢失初始化响应时不能声称已清理未知 UploadId。
- 删除不传 VersionId，不永久清空历史。写请求失败/取消不代表回滚，接口不会盲目重放。
- 10 GiB 缓存是软上限，0 关闭自动回收；临时下载、固定、修改和活动内容可超限。不解除固定来达标。
- 用户服务地址与桶只保存在忽略的 artifacts/service-targets.local.json，未成为公共默认值，未提供或提交任何真实密钥。
