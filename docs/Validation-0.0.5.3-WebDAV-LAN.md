# HybridMount 0.0.5.3：专用 LAN WebDAV 测试记录

日期：2026-11-01，Windows x64。用户追加要求为 1–3 小时实际功能测试，包含 500M 大文件、缩略图、从 HybridMount 写入后独立核对，以及从 DAV 上传／修改／删除后检查 HybridMount。仅“下载”共享及其子目录允许读写；服务器根目录不允许写入。凭据仅保存在内存与当前用户 DPAPI 密文中，不提交密码。

## 已完成

- `build/Test-WebDav.ps1` 与 `LiveWebDavRunner` 提供显式 60–180 分钟测试入口，普通单元测试不联网。唯一远端集合创建成功后开始计时，独立 NTFS 根、SQLite 与测试注册表，不改绑原挂载。
- `-Extended` 增加真实 500 MiB 文件、冷缩略图及损坏媒体；`-Focused` 用于短轮复验，报告 FocusedCompleted，不能替代长测时长。工作目录中的 stop.request 可正常取消并执行精确清理。
- 文件系统触发的新增／更新通过独立 HttpClient DAV GET 检查状态、长度和 SHA-256，再核对持久任务代次，不依赖本地缓存来证明远端上传完成。反向使用独立条件 DAV PUT／DELETE，检查刷新、冷占位、水合和删除结果。
- 修复真实服务暴露的文件 URI 问题：深度 0 元数据查询不追加集合尾斜杠；深度 1 集合枚举保留尾斜杠。严格文件 URI、特殊字符、零字节及临时清理回归已增加。
- `build/Repair-WebDavTest.ps1` 与 `LiveWebDavRecovery` 提供使用公开 API 的测试恢复入口。验证唯一根、完整清单、账号根摘要、探测内容及强 ETag；未知或变化内容拒绝清理，不修改产品 Foreign 保护策略。

## 未完成

- 产品并发打开／预览与“释放空间”之间出现原生 E_HANDLE 的具体窗口尚未修复；本版保留实际任务失败及复测结果，不放宽本地修改保护。
- WebDAV 临时项因元数据或强弱标签变化被保留为 Foreign 的生产恢复策略尚未改动；只对本次已记账测试内容执行显式核对清理。
- F04–F11 继续按开发计划排队，本批未修改其实现。

## 待测试

- 已完成本轮计时、单项和清理核对；后续需对 LAN-01／LAN-02 的产品修复执行故障窗口复测。本轮测试完成不表示这些后续问题已经修复。
- 实际 Explorer 自动选择缩略图处理器、预览窗格、开关视觉交互、安装升级、托盘正常退出及 ARM64 实机仍待测。服务端与 CFAPI／独立解码进程的检查不能替代这些项目。
- HTTPS 5008 的证书名称及信任链仍不通过；未关闭产品证书验证。本轮授权测试使用 HTTP 5007 的“下载”共享。

## 已测试

| 场景 | 实际结果及证据 |
| --- | --- |
| 共享权限 | `http://10.0.0.20:5007/下载/` Depth 1 返回 207；仅在其下建立唯一测试集合。此前根路径的 403／断开属于入口诊断，不计入功能时长 |
| 75 分钟主长测 | 4504.524783 秒，152 轮；原始结果 297 通过／32 限制／1 失败，失败及补测分别保留，全部本轮测试根清理已核对 |
| 500 MiB 文件 | 524,288,000 字节，由挂载目录文件系统写入触发自动上传；独立 DAV 完整读回、SHA-256、脱水、CFAPI 三处随机读取、固定后的完整摘要、再次脱水均通过，耗时 314,747 ms |
| 大文件独立摘要 | DFAE37167FC727E310DC627CF3A5CCED4EE7D4D412894AF44D149279CB1F6A70；保存于主运行 live-report.json 的 IndependentDavChecks，不由本地缓存推断 |
| F01／F02 | 真正保护释放后编辑 B，B 产生下一代并上传；排队目录替换为文件保持冲突。主运行及聚焦复验均通过 |
| 独立 DAV 入站 | 原始 DAV 创建、条件修改、删除；HybridMount 刷新后检查冷占位、InSync、水合新内容及干净本地项清理，通过 |
| JPEG／PNG／MP4／EXE | 独立 DAV 上传合成媒体及 16 MiB 带资源 EXE；原文件保持冷占位，后台生成 128 像素预览，PNG 分别为 171／192／435／13,492 字节。未以 Explorer GUI 验收名义记录 |
| 缩略图关闭／恢复、损坏媒体 | 最终聚焦轮全部通过；先关闭预热建立冷占位，再检查损坏媒体安全回退及重新开启缓存，不把预热句柄竞争混入冷预览起点 |
| 聚焦轮 | `f36cb36b-be80-4b15-a2a1-054baed3bd7b`：FocusedCompleted，62.8275814 秒，27 Passed、1 CapabilityLimited、0 Failed，精确清理通过；不记为长测满时长 |
| 能力边界 | 本服务 MOVE 条件能力未通过验证，记录 CapabilityLimited，未执行不安全移动；认证通过不代表所有写能力通过 |
| 自动化与恢复入口 | x64 Debug／Release 各 236/236。新增恢复测试覆盖正常清理、未知文件、探测内容变化与活动会话；拒绝分支均没有发送 DELETE |
| 旧运行精确恢复 | `33eada7f-dc05-4858-bf0f-8a4827b7fb11` 原报告保留；一个 Foreign 探测文件经精确日志、作用域、摘要和强 ETag 核验后清理，远端根及本地注册均已移除，recovery-cleanup.json |

主长测已于 2026-11-01T08:20:38.4422481+00:00 结束，实际 **4504.524783 秒（75.08 分钟）**，152 轮，原始报告为 CompletedWithFailures：297 Passed、32 能力／前置条件限制、1 Failed。已创建的全部本轮远端根均已移除，本地测试根已注销；见 0053-lan-final-root-audit.json。

整轮没有记为零失败。原始运行保留了两类问题：EXE 首次脱水返回 E_HANDLE；早期观察器使用旧元数据或要求弱／强标签令牌相等而错误等待。后者只修正测试观察方式，产品仍严格校验版本；F01 的第 2 代内容另经原始 DAV GET 与 SHA-256 确认。早期短轮、取消及失败报告均保留，不合并伪装为一次通过。

2026-11-01 07:19 UTC，私有反射清理辅助命令被 Defender 拦截。随后记录 ActionSuccess=true、DidThreatExecute=false、IsActive=false；脚本未落盘，安全设置未更改，也未将告警认定为误报。已放弃该方案；公开、受测的恢复入口实际清理通过。证据 0053-defender-incident.json、0053-public-recovery-real.log。07:45 UTC 的补充只读检查又被 AMSI 拒绝，分类为 TrojanDownloader:PowerShell/Gootkit.A；该记录 DidThreatExecute=true、ActionSuccess=true、IsActive=false，辅助文件未落盘，不能简单记成完全未执行。已停止在 PowerShell 中读取凭据并拼接 curl 配置的辅助检查，记录见 0053-defender-followup.json；未重试被拦截命令或更改安全设置。

所有证据位于 artifacts/validation。主长测目录为 `0053-dav-lan-download-addf9888cc5b4582a4161033a1c2503c`；最终聚焦目录为 `0053-dav-lan-download-f36cb36bbe804b15a2a1054baed3bd7b`。服务响应、密文、运行副本和产物保持忽略，不提交凭据。

```powershell
./build/Test-WebDav.ps1 -Endpoint 'http://10.0.0.20:5007/下载/' `
  -WorkDirectory '<artifacts/validation 下的新隔离目录>' `
  -CredentialId '<DPAPI 凭据标识>' -Minutes 75 -Extended
```

测试器显式创建唯一集合是本轮授权验证操作；产品连接、测试连接、浏览及缩略图请求仍不应创建辅助目录。清理只针对本轮记录且经核对的内容，拒绝按名称前缀批量删除。
