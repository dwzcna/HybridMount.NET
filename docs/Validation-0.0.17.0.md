# 0.0.17.0 验收记录

基线265f7b5（0.0.16.7）。证据及版本化补丁位于artifacts/validation/00170-work/。本版本地提交，不推送。

## 实现

WebDAV／HTTPS、S3及群晖配置共用协议能力Expander，四态为支持、不支持、声明支持、--。只记录已有请求的结构化结果及精确范围；正文消费／必要语义校验后才报告支持，同范围实测不支持覆盖声明，不同范围结论矛盾显示--。只读测试不取用户正文、不新增写入探针；界面展开不调用网络。快照256条有界、250ms合并，独立于业务安全判断；不更改公共IPC、SQLite、注册表及.hmxbk格式。

草稿使用摘要与单调代次。同步输入事件保护地址／密码改后改回的迟到测试；空白／非法草稿安全失效，显示名称等无关输入不使证据失效。当前已连接表单保留实时证据，SQS配置和连接代次变化分别退役相关结果。三语言新增46键，合计1206键×3，原生8键×3。

## 已测试

- 最终x64 Debug／Release非打包编译：build-debug-final3.log、build-release-final2.log。
- 最终Debug／Release定向各139/139，0失败、0跳过：debug-final4.trx、release-final3.trx。覆盖协议能力模型／连接测试、DAV配额和范围、群晖、S3范围与分片、SQS、实际条件写和挂载展示。夹具核验只读请求集合、根与子目录范围、声明优先级、权限及部分失败、凭据修订、ABA／取消／重连、不可变上限与并发观察者、公共JSON不带内部快照。
- 最终独立真实WinUI Debug／Release各36组布局＋一组实际输入绑定检查：ui-debug-final3.json、ui-release-final3.json。使用产品能力控件、绑定和尺寸源码、真实三语言资源，覆盖三协议×明暗×600×480／1080×760、四态、历史和失效折叠；验证地址／密码／服务器发现的改后改回、无关字段、空草稿及失败测试展示。探针不启动产品业务服务或注册同步根。
- 最终x64／ARM64 Release自包含签名MSIX和包检查：package-x64-final.log、package-arm64-final.log。使用已有CN=dwzcn证书，未安装包或更改信任。
- ARM64测试程序集交叉编译：tests-arm64-build.log，0警告、0错误；未在ARM64执行测试。
- git diff --check通过。

签名包SHA256：

| 架构 | SHA256 |
| --- | --- |
| x64 | CC9F1747E65068302E0EE47B844839E2C65B2A569876E33B435F9F858F6399F1 |
| ARM64 | 30CBA854DFA194FAA2C0AEFD16E697D51CBB1E2F43D7E3A071C843ECF27C1AFB |

## 阶段失败与修正

首轮26项有3失败：两个隔离原生宿主仍为旧版本，一个范围夹具缺少ETag而触发既有版本保护；更新同版隔离宿主及夹具后通过。首次主编译FontWeights命名空间错误、纯模型主构造函数捕获告警均修正。UI探针先前引用主DLL导致重复PRI，改用隔离模型程序集并编译产品控件源码；探针追加服务器快照时误用internal类型，改用公共快照。

实际WinUI追加输入检查发现异步TextChanged合并会漏掉中间草稿（ui-debug-final.json、ui-release.json均失败）；产品改用同步BeforeTextChanging／PasswordChanging和显式失效代次后，最终两配置实际绑定及139项回归均通过。保留失败日志，早期通过不视为覆盖末次修正。

## 待测试及现场分析

完整产品表单保存／业务交互、高对比度、跨屏DPI、Windows11 21H2与ARM64实机仍待验收。独立探针不是完整产品或Explorer响应验收；不操作正式挂载、不重启Explorer。

用户提供的移除失败日志及截图另存只读分析removal-analysis/findings.md；本版未修改清空移除逻辑。后续原生诊断已确认复现中ParentPackageMismatch／0x80070005，身份及启动修复归入独立0.0.18.0，不在本版放宽校验。
