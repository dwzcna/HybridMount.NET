# 0.0.4.1 验证与交付记录

日期：2026-10-30；版本：0.0.4.1；Windows x64 主机。预算为开发／验证 60–100 分钟，外部认证等待另计；本轮开发与必要验证约 52 分钟（01:51—02:43 UTC），文档、提交与推送另计。

## 已完成

本节仅记录代码实现：

- WebDAV 可选 ETag 容错、XML／HTTP 统一归一化、强弱区分、完整快照降级，以及默认严格／WebDAV 等价格式版本比较。
- S3 安全结构化错误、服务端区域提示、证书／网络区分及伪成功响应拒绝；保持 HTTPS、Path-Style、SigV4 与未验证默认区域提示。
- 只读测试当前表单，支持超时、取消及防重复；不持久化、不注册根、不做写入探测或分片恢复。已知分片恢复独立，失败不阻断读取。
- 四阶段注册持久化、旧配置分类、从未注册条目的远端配置编辑，以及非法输入保留原有效配置。
- 注册前失败挂载的离线保留／清空移除、清空二次确认、连接／任务取消和串行锁、迟到连接与根替换保护、链接不跟随及移除后清理。
- 模式固定和中断后显式重试保持有效；配置、凭据及索引清理后保留任务历史。三工程、IPC v2、x64／ARM64 和 0.0.4.1 版本保持统一。

## 未完成

- 本轮范围无待实现代码；S3 真实服务认证未通过，列入待测试。自动双向同步、写回及后台周期重扫属于后续范围。

## 待测试

- CSTCloud 当前已保存配置与凭据：HTTPS、Path-Style、SigV4、us-east-1（未确认），只读请求仍为 401 / Unauthorized；无服务错误细分、请求 ID 或区域建议。需确认服务端签名上下文、凭据来源及桶／前缀授权后重新完成真实 S3 枚举、占位及读取闭环。
- 其他 WebDAV 和 AWS／MinIO 端点兼容；坚果云写入、条件修改能力及大目录／大文件压力。本轮真实服务仅执行读取。
- GUI 中测试按钮、取消／超时／防重复、编辑限制、失败提示和移除二次确认；Explorer 角标／菜单、安装升级及 ARM64 实机。
- 注册丢失且实际残留云占位的系统故障注入。本机主动注销后系统解除云标记，未复现该残留条件；代码在实际检测到残留或物化索引证据不足时保留配置并拒绝操作。
- 更多权限／ACL、恶意并发替换、长路径及网络高压力场景。

## 已测试

证据位于忽略目录 artifacts/validation，以下均为本版本最终结果：

| 范围 | 实际结果 | 证据 |
| --- | --- | --- |
| x64 Debug 全量 | 80/80 通过，WinUI 非打包与原生扩展编译通过 | 0041-all-debug.trx、0041-debug-build-final.log |
| x64 Release 全量 | 80/80 通过，包含真实 DLL COM／NamedPipe 回归 | 0041-all-release.trx、0041-release-x64-build.log |
| WebDAV 真实 HTTP 回归 | 空／空白／裸／弱／强 ETag、响应差异、旧令牌格式、范围／整文件缓存、强冲突及既有 XML／条件写回归通过 | WebDavTests，最终 TRX |
| S3 真实 SDK HTTP 回归 | HMAC SigV4 核验；401／403／签名／桶不存在／区域头及 XML／非标准 401／200 HTML 拒绝；只读探测、取消／超时及 TLS／网络区分通过 | ConnectionTests、S3Tests，最终 TRX |
| 注册与移除 | 注册前失败的保留／清空、旧配置默认、非法编辑保留、占用失败重启重试、迟到连接、根替换、物化索引恢复阻断、其他挂载归属及内外链接保护通过 | FailedMountTests、ClearRemovalTests，最终 TRX |
| 恢复隔离 | Probe 不处理已知分片；单独恢复只处理已记录 UploadId；损坏恢复记录不阻断真实 S3 HTTP／CFAPI 文件读取 | ConnectionTests、S3Tests，最终 TRX |
| 坚果云真实服务 | 使用本机 DPAPI 凭据认证／枚举成功，独立 NTFS CFAPI 占位和 176,202 字节水合通过，内容与直接读取快照一致；Range 能力为 Unsupported，使用完整缓存 | 0041-real-webdav.log、0041-real-services.json |
| CSTCloud 真实服务 | 只读请求仍返回 401 / Unauthorized；请求 ID 和区域提示为空，认证及读取未通过 | 0041-real-services.json |
| x64 非打包进程 | 静默启动、静默重复启动、正常重复恢复窗口、非挂载缓存请求拒绝且测试文件不变，通过 | 0041-launch-x64-Unpackaged.txt |
| 实际注册阶段 | 新版主进程启动后，已有 WebDAV 为本产品 Registered 根，失败 S3 为 NotRegistered；保留用户定义及凭据 | 0041-live-mount-state.json |
| x64／ARM64 签名 MSIX | 0.0.4.1、自包含、PE 架构、资源、CloudFiles／菜单／启动声明、AWS DLL 和许可证、签名验证通过 | 0041-release-x64-build.log、0041-release-arm64-build.log |
| 二进制版本 | Debug／Release 主程序、双架构扩展和测试统一 0.0.4.1 | 0041-binary-versions.json |
| 桌面工具尝试 | node_repl 初始化遇到 Windows sandbox setup refresh 错误，重置重试仍为 trusted Node process exited unexpectedly；未执行 GUI 输入 | 0041-gui-attempt.txt |

签名安装包：

- artifacts/packages/x64/HybridMount_0.0.4.1_x64_Test/HybridMount_0.0.4.1_x64.msix
- artifacts/packages/ARM64/HybridMount_0.0.4.1_arm64_Test/HybridMount_0.0.4.1_arm64.msix

主程序运行检查保留新版 x64 非打包实例，供手工验收。独立真实验证根已离线清空、注销并清除临时内容；本轮早期回归遗留的五个隔离 CFAPI 根已按身份及绝对路径核对后清理。用户失败 S3 挂载未被自动删除。

已知边界：

- 非标准标签不会被加引号升级成强标签；缺少强验证器时，整文件缓存提供单次下载快照，大小／时间等线索不能证明同大小、同时间的远端内容未变，也不提供可靠条件覆盖。
- 版本等价仅接受协议可证明的格式差异，不把弱标签、裸值和强标签视为相同内容版本；其他协议默认严格比较。
- S3 的 401 不能单独证明密钥错误或区域错误，当前未确认根因；不盲试区域，不把诊断改进算作服务已连通。
- 自动分片恢复仅针对本程序的已知记录。测试连接不访问挂载仓库，不触发该恢复，也不验证远端写入能力。
- 根身份或提供程序归属不符、注册查询失败或残留云占位时拒绝删除。清空部分完成不会回滚，重启不自动继续删除。
- ARM64 只验证交叉构建与包内容，没有 ARM64 主机运行结果。GUI、Explorer 及安装升级均未记为通过。
