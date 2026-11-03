# HybridMount 0.0.8.3 验收记录

2026-11-04，Windows x64，.NET SDK 10.0.401／VS 2026／Windows SDK 26100。三工程、程序集及 MSIX 统一 0.0.8.3，SDK／IPC v2／SQLite v6／备份 v3 不变。预算 45–90 分钟，真实服务与 GUI 等待另计。

## 已完成

- 取消、TLS 握手重置、证书策略失败、超时及普通网络分类；仅保存安全阶段、异常类型／HRESULT 链、HTTP 传输分类、Socket／原生码及证书枚举，不输出原始消息、服务正文、URL 或凭据。
- 请求关联的严格证书策略，任何平台链／域名错误均拒绝。TLS／元数据／REPORT／配额受 120 秒所有者总期限，文件流受 120 秒无进展期限；表单仍 30 秒，可取消。
- 普通只读明确瞬态最多三次，延迟 200／400ms，原条件、范围及有限 XML 正文保持一致；未知 TLS 原因和证书失败不即时重试。严格预览仅一次，不放大原 32 请求预算；已交付部分正文不重放。
- 写入不重发，失败及取消仍为结果待核对；背景检测和水合同类故障合并，成功记录恢复，证书错误暂停自动检测并说明修正后重连。

## 未完成

本版范围代码已实现。用户原错误是异常传播现场，尚未确认服务端根因或产品崩溃；不能把本机受控 TLS 通过写成原服务已修复。

## 已测试

| 范围 | 实际结果与证据（artifacts/validation） |
| --- | --- |
| 针对性 | 59/59，0083-targeted-final.trx／0083-targeted-final.log；TLS／重试／严格预览、元数据／正文／配额期限及退出取消 |
| 本机真实 TLS | 随机 loopback 端口，实际 ClientHello 后远端重置；自签名链拒绝，另一证书同时域名不匹配；无系统信任安装或 HTTP 降级。慢握手期限、表单取消与停止均受控通过 |
| 真实坚果云 | 0083-real-dav-bfe0778bdcbe46edb8fe0b3e1e8e0b8f/report.json／0083-live-dav.log。严格平台证书验证和只读查询通过；配额属性缺失仍未知，无远端写入／注册／正式 DB 打开 |
| 完整 x64 | Debug／Release 各 384/384，0 失败、0 跳过：0083-contracts-x64-Debug.trx／0083-contracts-x64-Release.trx；0083-debug-final.log／0083-release-final.log |
| 签名 MSIX | x64／ARM64 0.0.8.3 签名、清单／PE／依赖／许可／资源、自包含与缩略图门控通过：0083-package-x64-final.log／0083-package-ARM64-final.log |
| ARM64 测试工程 | 交叉编译成功，0 警告／错误：0083-tests-arm64-final.log；未在 ARM64 实机执行 |

首轮证书夹具因 Windows Schannel 无法使用原临时私钥，实际只形成普通 TLS 失败；0083-network-second.trx／0083-certificate-diagnostic.trx 保留失败。夹具改用随证书释放的用户临时密钥容器，未安装证书信任，0083-certificate-schannel.trx 两项通过。旧退出测试断言精确 IOException 与新增诊断派生类型不符，更新为明确 Timeout 诊断后复测通过；0083-network-final.trx 的失败不改写，后续 0083-network-fixed.trx 为 58/58，再增加严格预览预算回归后最终 59/59。

模拟故障明确验证原始 URL／凭据消息不进入诊断、三次上限及恢复、REPORT 原令牌正文／GET Range 和 If-Match 保留、发送后的 MKCOL 失败与取消不重发且保留未知结果、同类 100 次并发错误合并及恢复只记录一次。模拟写入无真实服务副作用。

产物：artifacts/packages/x64/HybridMount_0.0.8.3_x64_Test/HybridMount_0.0.8.3_x64.msix；ARM64 对应目录。签名证书 3FB1D78271BA8F6EDD3CE163F57D93D13FEB83C0。

## 待测试

- 原用户服务的 TLS 重置现场、反向代理及证书链诊断后实际恢复；本轮未访问用户新增 D:\myweb 或已取消的 NAS，未为验证改动正式配置。
- Computer Use 的 sandbox helper 仍无法初始化，GUI、Explorer、安装升级、ARM64 实机、真实托盘退出和调试器继续运行后的交互未执行。本版非打包启动因新增自动连接未注册挂载而保留现场，未记通过。
- 真实 AWS SQS、历史 SHELL-01 及原审查待办维持各自边界。调试器“引发时中断”仍可停于 HttpClient 内部异常，验收看继续后任务状态、收尾及未处理异常，不能以首次异常认定崩溃。

参考：.NET HttpClientHandler.ServerCertificateCustomValidationCallback 的策略结果用于严格诊断；参考资料不算测试证据。
