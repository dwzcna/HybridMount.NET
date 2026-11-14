# HybridMount 0.0.6.1 验收记录

日期：2026-11-02—03（Asia/Shanghai），Windows x64，.NET SDK 10.0.401、Visual Studio 2026 与 Windows SDK 10.0.26100.0。产品、程序集、扩展、测试与 MSIX 统一为 0.0.6.1。开发及验证预算 90–150 分钟，GUI／实机等待另计。

## 已完成

- 挂载页“新增挂载”旁增加导出与导入；共享 ContentDialog 提供多选、密码确认、解密预览、目录重选和逐项结果，忙时禁止重复提交，窗口释放请求取消。
- 专用 DTO 只包含名称、本地目录提示、WebDAV／S3 连接参数、对应凭据、AutoConnect 和 ThumbnailsEnabled。运行挂载 ID、根身份、索引、缓存、任务、移除状态不进入文件。默认凭据链仅保存模式，不解析环境或系统凭据。
- 导入只新增，重复跳过；按现有验证检查 NTFS 空目录、路径边界、重解析点、自身目录、现有系统根及本批冲突。新 GUID、当前目录身份、未注册阶段，凭据以当前用户 DPAPI 重新保存。不创建协议客户端、不联网、不注册、不上传，保存后可手动连接；本进程的启动恢复也跳过新导入项，下次启动恢复原自动连接设置。现有 AddAsync 保持创建并连接。
- 备份／预览／保存纳入应用任务所有权，配置串行锁防止同时保存重叠目录；每项提交，取消保留已成功项，停止后拒绝迟到保存；保存失败清理该项凭据及中途定义。
- 两个指定测试同步根经公开离线 clearAndRemove 清空注销，完成对应测试配置、索引、凭据及精确 GUID 目录的清理。只操作已核对的测试身份，不连接旧测试服务、不水合、不删除远端。保留现有三个工程、SDK、IPC v2、SQLite v4 和缩略图门控。

## 未完成

本版范围代码已实现。SHELL-01 历史阻塞根因及 F04–F11 等既有工作未在本版修复；本次全量通过不代表历史缺陷的确定性修复。跨机器实际迁移与 GUI／实机属于下列待测试项。

## 待测试

- 文件选择器、导出全选／取消勾选、确认密码、解密预览、目录重选、重复提交／取消及部分结果的实际 WinUI 交互。
- 使用签名包在另一机器及打包／非打包模式之间真实迁移；自动连接选项恢复后的实际下次启动。密码学与保存测试在本机不同隔离仓库完成，没有冒充跨机器实测。
- Explorer 中两个旧入口消失的视觉核对、安装升级、正常托盘退出，以及 ARM64 实机；本轮仅 ARM64 交叉构建。
- SHELL-01 的历史 11 项失败根因、确定性交错及收尾；正式缩略图注册保持关闭。
- Computer Use 初始化／重试分别返回 `trusted Node process exited unexpectedly; kernel reset, rerun your request` 和 `node_repl kernel exited unexpectedly; windows sandbox failed: helper_unknown_error: setup refresh had errors`。没有进行 GUI 输入或采用 PowerShell UI Automation 替代；0061-gui-tool.json。

## 已测试

全部证据位于忽略目录 artifacts/validation。测试配置与目录均隔离，不批量提交用户挂载已有改动。

| 范围 | 实际结果及证据 |
| --- | --- |
| 完整自动化 | 最终 x64 Debug／Release 各 270/270，0 失败、0 跳过；0061-debug-final.trx、0061-release-final.trx，以及 0061-debug-final3.log、0061-release-final2.log |
| 新增备份回归 | 34 项：WebDAV／S3 往返、中文路径／密码、密码空白、临时令牌、默认凭据链、格式独立解密、随机盐／nonce、篡改／错误密码／格式／长度／超限、取消、原子替换失败、DPAPI 损坏拒绝导出；包含在最终全量结果中 |
| 仅保存导入 | 重复／批内重叠／并发同目录、同远端不同目录、未勾选、非空／缺失／数据目录／外部链接、无效协议／地址、部分失败、取消／退出后拒绝；已有配置与文件不变、新 ID、DPAPI 可读，断言文件源创建、云根注册、索引及上传为零 |
| 启动恢复交错 | 新测试先确定性复现导入后本次恢复连接了 1 次，修复后本次为 0；服务重建后仅自动连接条目调用工厂 1 次。0061-import-startup-before.trx 为保留的修复前失败，0061-backup-final.trx 为修复后 34/34，最终全量亦通过 |
| 旧测试根清理 | 两项公开 clearAndRemove 状态 Completed，本地空根、原生／WinRT 注销后精确删除测试目录／配置；0061-old-test-root-cleanup.json、0061-cleanup-final.json |
| 正式挂载保护 | D:\wdavtest、D:\s3test 根身份和归属不变，待上传及潜在本地改动均为 0；0061-original-roots-final-before.json、0061-original-roots-final-after.json |
| 实际非打包 | Ready、静默无窗口、静默重复实例、普通重复实例恢复窗口、非挂载缓存请求拒绝；0061-launch-x64-Unpackaged.json／txt／log。无在途上传后只结束本轮进程对象，是受控中断，不代表正常托盘退出码 0 |
| 构建及签名包 | x64 Debug／Release 实际 WinUI 构建、ARM64 测试交叉编译、双架构签名 MSIX 的版本／资源／PE／自包含／门控及包内程序集与最终输出一致；0061-tests-arm64-build-final.log、0061-release-x64-package-final.log、0061-release-arm64-package-final.log、0061-final-packages.json |

### 备份格式 v1

文件上限 8 MiB，最多 1000 条，JSON 深度 16，拒绝未知字段／版本和异常长度。备份密码最少 8 个 UTF-16 字符，不去除空白；PBKDF2-HMAC-SHA256 固定 600,000 次派生 AES-256 密钥。整个载荷加密，只在内存处理明文；密码字节、明文字节和派生密钥最终清零。GCM 认证失败和错误密码统一提示“密码不正确或文件已损坏”。

| 偏移 | 长度 | 内容 |
| --- | --- | --- |
| 0 | 8 | ASCII HMCONFIG |
| 8 | 4 | 小端格式版本 1 |
| 12 | 16 | 随机盐 |
| 28 | 12 | 随机 nonce |
| 40 | 4 | 小端密文长度 |
| 44 | 16 | GCM 认证标签 |
| 60 | 变长 | AES-256-GCM 密文，前 44 字节作为 AAD |

落盘仅写加密临时文件，刷新后同目录原子替换；写入／取消测试验证原备份保持不变、临时文件清理。DPAPI 解密失败不会被当作空凭据成功；默认凭据链模式刻意不读取原机器凭据。独立使用 .NET AES-GCM 解密的测试验证与当前编解码器互通，未连接真实账户执行导入后连接。

### 精确清理范围

| 挂载 ID | %TEMP%/HybridMount.HttpTests 下已清理目录 |
| --- | --- |
| 3b69c293-8a8d-430f-a031-e056435d16ad | 644d95a7498a4c9ba957c525a792eae4（原 view 根） |
| ddeca661-5942-48b7-b90e-87bb8c806e44 | e1c08d2f881c405db9ee499c96563da4（原 view 根） |

执行前核对保存定义、目录身份和原生 MountId，禁用协议工厂；公开 clearAndRemove 完成后确认空根、原生标记和 HKLM SyncRootManager 记录消失，再以不跟随链接的删除处理这两个精确 GUID 目录。已发送 Shell 更新通知；Explorer 视觉入口没有实际核验，不记 GUI 通过。

### 历史 Shell 失败单列

下表保留 **0.0.6** 的实际失败结论，原始证据见 docs/Validation-0.0.6.md。本版最终两轮全量均无失败，但没有修改这些 Shell／原生管道代码，不能把一次通过当作根因修复，旧失败不改记通过。

| 0.0.6 历史用例 | 数量 | 历史失败与本版边界 |
| --- | --- | --- |
| PreviewTests.UnavailablePreviewDataFallsBackWithoutHydration | 4 | 首项超时、后三项管道占用；旧基线同样复现，原因仍待定位 |
| PreviewTests.ActualShellBindingIsMeasuredSeparatelyFromIdentityOnlyHandler | 1 | Shell 绑定后超时，未修复相关实现 |
| PreviewTests.ColdAndWarmCloudExePreviewsNeverHydrateAndLocalExtractionSurvivesDehydration | 1 | 管道占用，未修复相关实现 |
| PreviewTests.RealShellImageFactoryReportsAutomaticExeReads | 3 | 系统图像工厂超时，未修复相关实现 |
| 当时的预览身份QueueTests.EnabledColdImageQueuesInBackgroundWhileNativeCallbackReturnsGenericIcon | 1 | 管道占用，未修复相关实现 |
| NativeExtensionTests.NativeComAndPipeRoundTrip | 1 | 管道占用，未修复相关实现 |

### 构建入口与交付

```powershell
./build/Build.ps1 -Platform x64 -Configuration Debug -Deployment Unpackaged -Test
./build/Build.ps1 -Platform x64 -Configuration Release -Deployment Unpackaged -Test
dotnet build HybridMount.Tests/HybridMount.Tests.csproj -c Release -p:Platform=ARM64 --no-restore
./build/Build.ps1 -Platform x64 -Configuration Release -Package -CertificateThumbprint '<当前用户证书指纹>'
./build/Build.ps1 -Platform ARM64 -Configuration Release -Package -CertificateThumbprint '<当前用户证书指纹>'
```

下列正式包使用本机开发证书签名、无时间戳，不自动注册实验缩略图处理器。包内主程序及扩展摘要与本轮最终构建输出一致。产物保留在 artifacts，不纳入 Git。

| 架构 | 签名 MSIX | SHA-256 |
| --- | --- | --- |
| x64 | artifacts/packages/x64/HybridMount_0.0.6.1_x64_Test/HybridMount_0.0.6.1_x64.msix | 2F222C1906849FAA02E098544587446DC60C3767DCFC68C07CA808268F08E364 |
| ARM64 | artifacts/packages/ARM64/HybridMount_0.0.6.1_arm64_Test/HybridMount_0.0.6.1_arm64.msix | BB52B8CF961342800C5FC0047068B34C335BA075ED747061CC68814D347E567E |
