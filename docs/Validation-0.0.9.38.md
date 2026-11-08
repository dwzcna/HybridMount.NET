# HybridMount 0.0.9.38 验收记录

日期：2026-11-08。最终范围为“文件改名进入排除规则后，云端同步改名并取回本地”。代码与自动验证已完成；正式服务、桌面和实机项目按实际保留待测。预计6—10小时，人工等待另计；本地提交，不推送。

## 已完成代码与保护边界

- 非排除名称改为排除名称时，条件移动已确认的原云端版本；目标重名或版本变化不覆盖。原上传已发送／结果不明先核对回执；未发送的旧工作停止，没有可靠映射时只排除本地上传。
- WebDAV分别验证源If-Match与Overwrite:F。S3复用条件复制／云端正文回退，目标回执先持久保存再条件移除源，保留版本历史；部分完成重试不会重新复制。DAV空MOVE／DELETE及S3每次修改调用拥有发送保护，回环断连接验证目标请求单次发送。
- JSON日志独立于可清任务历史，绑定SID、根与NTFS FileId、连接作用域、原版本、目标回执及阶段。连续改名原子交接；新建旧名称不继承基线。网络、下载和摘要在根锁外执行，取消及会话变化均重验。
- 远端改名确认后重绑下载映射，补齐缺失内容，保留本地未上传修改；刷盘及摘要／身份核对后CfRevertPlaceholder并标记EXCLUDED。新云路径与原版本保留用于回显／冲突识别，后续本地修改不上传。原失败属性仅按拥有身份精确清除，排除及其他属性保持。
- 任务页提供阶段、原因、定位、显式重试、只读重新核对和取消。重新核对不会执行MOVE／DELETE或触发正文下载；取消不撤销已完成的云端步骤。后续远端变化进入冲突视图并保全本地。
- 缺移动回执、两端存在但归属无法证明等情况保留结果待核对；不按名称或跨路径ETag相同就猜测所有权。可靠回执与两端状态确认后才继续。此保护行为不等于远端操作必定回滚。
- 保留三个工程、SDK、文件源公共接口、IPC v2、SQLite v6、配置及备份v5格式。新增内部日志Schema2；开发期未发布的删除日志Schema1不会被当作移动授权继续执行。缩略图与0.0.9.33专项均不扩展。

## 实际自动验证

| 项目 | 实际结果 | 证据（artifacts/validation/） |
| --- | --- | --- |
| 初始关联／日志与扫描 | 28/28 | 00938-batch1/00938-batch1.trx；旧删除语义，不能作为最终改名验收 |
| 改名首轮 | 16通过／1失败 | 00938-move-first/00938-move-first.trx；重启时序断言及清理异常，已修正并复测 |
| 内容／连续改名首轮 | 23通过／2失败 | 00938-move-boundary/00938-move-boundary.trx；部分缓存构造及普通文件原映射已修正 |
| S3／HTTP保护阶段 | 56通过／1失败，随后S3 4/4 | 00938-protocol-guards/00938-protocol-guards.trx；00938-s3-once/00938-s3-once.trx |
| 协议／排除／扫描针对性 | 108/108 | 00938-move-targeted/00938-move-targeted.trx |
| 旧失败属性及相关 | 55/55 | 00938-final-targeted/00938-final-targeted.trx |
| Debug广泛首两轮 | 各1279通过／1失败 | 00938-broad-Debug/；00938-broad-Debug-fixed/；核对与终态重试边界已修正 |
| 最终x64 Debug广泛 | 1280/1280，失败／跳过0 | 00938-final-Debug/00938-final-Debug.trx |
| 最终x64 Release广泛 | 1280/1280，失败／跳过0 | 00938-broad-Release/00938-broad-Release.trx |
| 最终Debug／Release非打包构建 | 通过 | 00938-review-debug-build.log；00938-review-release-build.log |
| x64／ARM64签名MSIX | 签名及实际包内容检查通过 | 00938-package-x64-final.log；00938-package-ARM64-final.log；package-x64.sha256.txt；package-ARM64.sha256.txt |
| ARM64测试工程 | 交叉编译通过，未执行ARM64测试 | 00938-tests-arm64-final-build.log |
| Compile登记 | 主230／测试318，重复及遗漏0 | 00938-final-compile-audit.json |

广泛回归后最终复核仅调整连续改名接管状态的中文显示、两处失败文字及注释，未变更状态机或协议行为；相关界面／状态回归24/24通过（00938-review-words/00938-review-words.trx），Debug／Release及双架构签名包均按最终文本重建。

广泛回归固定使用四测试线程；过滤FailedMountTests、OrphanedMountRecoveryTests、ClearRemovalTests，不补跑0.0.9.33已停止专项。中间构建的可空检查、诊断工厂重复注册、旧删除方案61/63及后续传输定位日志均保留，不改写为通过，也不混入最终验收计数。

已覆盖：零字节、完整／固定缓存、仅联机、真实部分缓存、本地未上传修改及后续编辑；尚未上传、在途回执、历史清除、重启恢复；连续改名、改回、立即新建同名文件；目标已存在、远端版本改变、401／403、条件与禁止覆盖能力不足、下载失败、FileId替换和连接作用域变化。核对本地字节、FileId、普通文件形态与ProviderState=9；没有额外本地正文上传。只读核对期间显式重试受保护，已发布终态仍可接纳下一次请求。

## 测试现场收尾

独立非打包审计确认IdentityStatus=15700。精确清单包含260个路径、255个根ID；原生检查、SyncRootManager、NameSpace、CLSID、对应隐藏值和临时包引用均核清。首轮失败重启用例遗留的一个根通过原创建时间、失败TRX、测试定义及NTFS根身份归属核验后，仅清空该隔离根的3项并注销，没有连接远端；原测试定义备份后精确删除。另备份、删除261个已无引用的32位CLSID，独立读回通过。

证据：00938-failed-root-definition-proof.json、00938-failed-root-cleanup.json、00938-exact-test-definition-backup.json、00938-exact-clsid-backup.json、00938-exact-clsid-cleanup-result.json、00938-final-root-audit.json及00938-root-audit-final.log。未归属精确清单的32位测试CLSID仍有28430项，保持原样；不能宣称全机无残留。正式记录及其他账户保持，工具未重启Explorer。

## 待测试与限制

- 当前真实WebDAV／S3服务的条件改名及取回，尤其服务商MOVE、条件复制与版本化桶实际兼容性。
- 最终MSIX正式安装／升级、实际非打包WinUI启动、任务页窗口布局、筛选、重复点击及完整交互。
- Explorer目视确认排除图标与同步箭头收尾；属性读回和原生测试不能代替目视验收。
- ARM64实机；交叉构建及签名检查不代表执行通过。
- 0.0.9.37的Explorer自动缩略图绑定仍未完成并保持门控，0.0.9.33已停止专项继续停止。

没有操作正式挂载或真实服务，没有访问已取消NAS；需要用户响应的项目按要求保留待测。产物位于artifacts/packages/x64/HybridMount_0.0.9.38_x64_Test/及artifacts/packages/ARM64/HybridMount_0.0.9.38_arm64_Test/，实际文件及哈希以本轮包日志为准。
