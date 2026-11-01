# HybridMount 0.0.5.3 验收记录

日期：2026-11-01，Windows x64，.NET SDK 10.0.401。产品、程序集及 MSIX 统一 0.0.5.3。开发及自动验证预算 3–5 小时，真实服务、GUI 和实机等待另计。

## 已完成

- F01：在保护引用和同一原生句柄下核验、转换、设置同步并取得稳定提交戳；索引、任务和 ObservedStamp 同事务保存，释放后编辑不被吸收，旧 Completed 脏记录可恢复下一代。
- F02：扫描、准备、父目录准备、发送及原生／索引提交校验类型；文件／目录变化统一 Conflict。工作器不能覆盖新确认的冲突；已发送请求保留回执和快照，恢复原类型需显式重试。
- F03：保留 S3 复制／上传回执，目标类型、长度、强 ETag 和双方可用 VersionId 核对后才条件删源；缺失验证器、替换、取消或失败保留源。服务条件删除不支持时明确报告目标已核对、源保留；移动非原子操作。
- 增加显式真实 WebDAV 长时测试器与运行脚本，默认 65 分钟，输入 60–180 分钟，独立凭据、远端集合及 NTFS 根；普通测试不自动访问外网。本地自检验证功能矩阵、取消、时长报告和精确清理。
- 保留三个工程、AWSSDK.S3、IPC v2 和 SQLite v4。F04–F11 只登记待办。源码压缩包没有纳入补丁或提交；它在执行途中不再位于原路径，已通知用户，未执行针对该包的移动／删除命令。

## 未完成

F01–F03 及文件 URI 兼容修复已实现。F04–F11、LAN 并发脱水 E_HANDLE 和探测 Foreign 恢复策略尚未修复，继续保留审查边界与后续验收场景。真实 WebDAV 测试服务的权限／共享路径不是已确认的产品代码缺陷。

## 待测试

- LAN-01 并发脱水 E_HANDLE 和 LAN-02 探测 Foreign 恢复仍待产品修复及复测；已完成的本轮长测、补测与清理不等同于这些问题修复通过。
- 当前 R2 的可靠条件删除探测不通过，完整移动未记通过；不使用无条件删源绕过限制。真实服务并发替换、网络故障及大对象压力仍按实际后续执行记录。
- 真实 GUI／Explorer、正常托盘退出码 0、安装升级及 ARM64 实机。Computer Use 初始化和重置后重试均因 Windows sandbox helper setup refresh 失败，未进行 GUI 输入，也未用 PowerShell UI Automation 替代。
- WebDAV 条件 MOVE 探测出现远端已不存在而 Foreign 临时记录仍保留的观察，尚未将其外推为远端文件残留；记录完结与重启策略待核对，保护规则未放宽。

## 已测试

| 范围 | 实际结果与证据 |
| --- | --- |
| x64 Debug／Release | 各 236/236，无失败／跳过；0053-all-x64-Debug.trx、0053-all-x64-Release.trx |
| F01 | 保护释放后关闭 B，代次尚未扫描时观察戳仍来自 A；B 后续上传。覆盖提交前中断、重建恢复、旧 Completed 脏戳和同事务代次保护 |
| F02 | 双向类型变化覆盖排队、准备、发送、远端提交和发送中扫描；冲突持久、显式重试、重启、错误回执及原生转换拒绝，检查发送前无目标写入 |
| F03 | 复制及上传回退均覆盖同长度不同内容、仅 VersionId 变化、弱／缺失 ETag、HEAD 失败、取消、目录替换及条件删除不支持；不发送用户源 DELETE。正常移动、目录部分结果和无目标 VersionId 的可靠 ETag 路径通过 |
| 真实坚果云／R2 | 各自隔离本地根与唯一文件，文件系统触发新增、SHA-256 读回、精确清理通过；DAV 临时项为零，无远端测试目录；0053-real-upload.json |
| 真实 R2 更新与移动边界 | 创建、条件更新和摘要通过；移动回退复制后因 ConditionalDelete=Unsupported 安全停止，清理前两端存在且摘要相同；全部测试数据清理。MoveCompleted=false，不记完整移动通过；0053-real-s3-move.json、0053-real-s3-delete-capability.json |
| 长时测试器本地自检 | 本地 HTTP 与真实 NTFS／CFAPI 完成功能矩阵及首轮后主动取消，准确显示不足请求时长，清理注册／本轮数据，原模拟数据不变；不代表真实服务 1 小时测试 |
| 最终非打包运行 | Ready、静默／重复实例、恢复窗口及非挂载缓存请求拒绝通过。仅结束本轮 Start-Process 原始进程对象，受控中断不作为正常退出；0053-launch-x64-Unpackaged.* |
| 原挂载 | 两个原根的身份、归属一致，无未决上传及潜在新改动；只读索引副本和原生元数据检查，不读取用户文件正文；0053-original-roots-final.json |
| 四个签名包 | x64／ARM64 正式及实验包的四段版本、自包含、PE 架构、资源与声明、签名和最终 DLL 一致性通过；0053-final-packages.json、0053-*-signature-*.log |
| LAN 单项与补测 | 500 MiB 文件系统上传、独立 DAV 摘要及 CFAPI 读取／水合／脱水通过；独立 DAV 新增／修改／删除和四类冷缩略图、损坏媒体复验通过。主运行 75.08 分钟、152 轮，原始 1 项失败及聚焦复验分别保留，全部本轮测试根已清理，详见专用记录 |

首轮全量 225/226 的失败来自故障注入在测试线程停止前重复执行；改为先停止调度再释放单次屏障后通过。长测器自检发现报告文件短暂读取竞争，增加有界重试；远端已不存在的 Foreign 临时记录单独保留观察，不伪称记录全部清除。辅助 PowerShell 首次 S3 创建将 null 绑定为空字符串，被安全拒绝；修正为 NullString 后重新执行，最终结果如上。失败证据保留，不冒充通过。

一次 Release 事务断言被活跃目录监控重新设置保护标记干扰；将该纯事务回归隔离到无监控的独立仓库后，最终 Debug／Release 均 236/236。首轮失败及修正后的日志分别保留。Defender 拦截及第二条执行标记记录在专用 LAN 报告；未将其认定为误报，未更改安全设置。文件 URI 修复后的坚果云目录补充只读检查被拦截，未记为执行或通过。

所有验证证据位于 artifacts/validation；辅助脚本、运行快照、产物与密文凭据保持忽略，不提交明文凭据。

```powershell
./build/Build.ps1 -Platform x64 -Configuration Debug -Deployment Unpackaged -Test
./build/Build.ps1 -Platform x64 -Configuration Release -Package -Test -CertificateThumbprint '<当前用户证书指纹>'
./build/Build.ps1 -Platform ARM64 -Configuration Release -Package -CertificateThumbprint '<当前用户证书指纹>'
./build/Build.ps1 -Platform x64 -Configuration Release -Package -ExperimentalThumbnails -CertificateThumbprint '<当前用户证书指纹>'
./build/Build.ps1 -Platform ARM64 -Configuration Release -Package -ExperimentalThumbnails -CertificateThumbprint '<当前用户证书指纹>'
```

本机开发证书签名，无时间戳；ARM64 为交叉构建，正式包不自动注册实验缩略图处理器。

| 架构／模式 | 签名包 | SHA-256 |
| --- | --- | --- |
| x64／正式 | artifacts/packages/x64/HybridMount_0.0.5.3_x64_Test/HybridMount_0.0.5.3_x64.msix | E714BD4D81A8968CC01748ACB2B16DB895CDB9A10D3E163549A462C5D9DB6A9D |
| ARM64／正式 | artifacts/packages/ARM64/HybridMount_0.0.5.3_arm64_Test/HybridMount_0.0.5.3_arm64.msix | 0F91055BE4F8003AAEA42189AE8D22E1CADC67D3CA03E6C45AF1F2DA2D5D786F |
| x64／实验 | artifacts/packages-preview/x64/HybridMount_0.0.5.3_x64_Test/HybridMount_0.0.5.3_x64.msix | A6C2B44C167BBD657B45A51BAF560BA4D666EF4EF7D14613B3B5E00341ED40FE |
| ARM64／实验 | artifacts/packages-preview/ARM64/HybridMount_0.0.5.3_arm64_Test/HybridMount_0.0.5.3_arm64.msix | EC05FA1DB11F753D0D8AA13680D12362C3032052E28C8482F590DCB27B250699 |
