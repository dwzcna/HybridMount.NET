# HybridMount 0.0.9.2 验收记录

2026-11-04，Windows x64，.NET10.0.401／VS2026／Windows SDK26100。开发及自动验证预算30–60分钟，GUI／实机另计。0.0.9.1已独立本地提交82c1e63，随后实施本版；此前保持未提交，现按0.0.9.3实施授权先核验并独立本地提交，不推送。三个工程、SDK、IPC v2、SQLite v6、配置定义与备份格式保持，程序集和MSIX均为0.0.9.2。

## 已完成代码

- 任务页新增清除失败和清除所有，保留清除已完成。失败包含Failed／PartialFailure；全部指Completed、Failed、PartialFailure、Canceled、Interrupted、Ignored和Superseded，运行、排队、等待、冲突、结果待核对与未知状态保持。
- 三个清理入口共享忙碌、防重入及按钮状态，应用后台执行后显示实际条数、增量刷新列表；窄窗口将清理按钮组放到下一行，避免截断。离页不取消已接纳工作，停止后拒绝调用，释放后不回写视图。
- 共用领域白名单和参数化单条DELETE，在仓库锁内清理整个operations表的相应历史，覆盖列表100条上限之外数据。上传实际阶段或远端恢复仍未完成时阻止删除旧展示行；空／未知状态保留。原ClearCompleted接口及语义兼容。
- 全部清理仅对已存在且不属于活动Job的旧Queued／Running记录提供精确孤立ID，不按这两个原始状态批量删除。当前上传、远端应用有独立持久保护，真正排队／运行任务继续可见。
- 失败终态的历史删除后，上传内存覆盖不再复活旧展示；底层上传状态、回执、快照、基线、索引和文件不删除、不改状态，不自动重发。重排队／新代任务仍重新显示。

## 实际验证

证据位于artifacts/validation，重叠批次不相加。

| 范围 | 结果与证据 |
| --- | --- |
| 最小编译 | x64 Debug WinUI非打包成功：0092-minimal.log；最终源码由完整Debug／Release再编译 |
| 针对性 | 33/33，零失败／跳过：0092-targeted-verified.log、0092-targeted-verified.trx；之后空状态用例由全量运行 |
| 完整x64 | Debug／Release各501/501，零失败／跳过：0092-Debug.log、0092-Release.log、0092-contracts-x64-Debug.trx、0092-contracts-x64-Release.trx |
| 签名MSIX | x64／ARM64的0.0.9.2签名和包检查通过，签名零错误／警告：0092-package-x64.log、0092-package-ARM64.log |
| ARM64交叉编译 | 测试工程成功，零警告／错误：0092-tests-arm64.log；未在ARM64主机执行 |
| 结构／版本 | 166个源Compile、201个测试Compile无重复，新领域规则在两工程各一次；统一版本、schema、协议及0.0.9.1配置视图未改：0092-compile-items.json、0092-test-compile-items.json、0092-structure-audit.json |

新增7项回归覆盖150条失败／部分失败跨列表上限与数据库重开；底层失败上传记录、保护索引、SHA256基线和实际快照内容保持，管理器重建不复活历史、新代重新出现；8种在途／未知上传状态和9种远端状态（含null）阻止误清理；20次并发重排队仍可见，退出后拒绝迟到清理；真实NTFS／CFAPI根内阻塞PROPFIND时，当前刷新Job保留、两个孤立中断ID精确移除、释放后刷新正常完成；页面三种按钮禁用／刷新及释放后不清理。旧成功历史回归保留。

## 首轮失败与修正

- 新测试的with初始化笔误导致首轮编译失败，日志0092-targeted.log；仅修正测试语法。
- 首轮实际针对性32/33：在途夹具连接已产生两条完成历史，错误假设本次只能删两条孤立记录，实际删四条。改为清理前的精确历史ID集合核验数量与逐项删除，同时保留活动Job及持久历史断言；33/33复测通过。记录0092-targeted-final.log及.trx。
- 故意注入null状态时，C#可空告警阻止首轮全量编译；显式标注测试的损坏数据注入后全量通过，未放宽产品模型或告警等级。记录0092-Debug-first-compile-failed.log。

## 产物

以下为2026-11-04任务历史清理批次产物，不包含2026-11-05界面说明清理；本次界面追加未重新打包。

- `artifacts/packages/x64/HybridMount_0.0.9.2_x64_Test/HybridMount_0.0.9.2_x64.msix`，SHA256 `2e969dca9ea45a2cee305834527444f3c53f95eab57a1386299135af04bb431f`。
- `artifacts/packages/ARM64/HybridMount_0.0.9.2_arm64_Test/HybridMount_0.0.9.2_arm64.msix`，SHA256 `87e9d9850ca07482153016bdb8fa3e07fdd5633d2b0e28608c86d27d034615cf`。

证书3FB1D78271BA8F6EDD3CE163F57D93D13FEB83C0。构建使用本机固定依赖缓存源，关闭该次在线审计；未更改安全设置或产品依赖。产品非二进制修改均先写补丁并经git apply --check应用；产物及辅助脚本不纳入源码。

## 待测试与限制

- 实际WinUI按钮／工具栏、连续点击、选择／滚动和结果提示、真实关闭／退出、Explorer、安装升级、非打包进程启动和ARM64实机。受控HTTP与CFAPI测试不替代这些验收。
- 没有清理正式用户历史、修改正式挂载配置、启动或停止用户实例，也没有真实远端写入；NAS10.0.0.20:5007测试保持取消。
- 清除所有仅作用于已结束展示历史；清理不会取消、重试或回滚任务。冲突、结果不明与待恢复内容继续保留，不能以清理按钮规避保护。
- SHELL-01、TEST-RUNNER-01、历史TLS／调试器、真实RFC6578／AWS SQS与其他审查待办继续独立记录。本次全量通过不表示这些根因已修复。

## 2026-11-05追加：移除界面常驻说明

### 已完成代码

- 五页移除首页介绍／副标题、卡片辅助说明、托盘常驻提示、挂载／任务／日志副说明和缓存回收说明；保留页面与分区标题、版本、操作及输入。
- 独立WebDAV／S3完整配置移除根切换说明、容量统计范围、区域默认值说明、SQS介绍及上传策略小字，同步移除后台赋值。容量、缩略图实际状态、远端检测／最近成功／错误、SQS状态和只读测试结果保留。
- 导出／导入删除默认及解密后帮助说明和通用操作指导；保留备份选项、目录校验、逐项状态、实际结果、取消、密码／文件错误及必要确认。
- 挂载和任务默认Status为空，后续Status赋值保持。视图层TextVisibility只决定是否显示空文字／反馈，不改变业务状态；协议反馈区观察ProbeBar.IsOpen、ErrorBar.IsOpen及StatusText.Text，任一有反馈即可显示。空区域连同边距折叠。
- 去除说明后同步收紧标题、底部反馈和备份密码区域布局；挂载卡片文件未改，第三行仍固定32 DIP。Tooltip、输入占位、字段标签与事件入口保持，原0.0.9.2功能、三个工程、版本、公共API、配置、SQLite v6和备份格式保持。

### 本轮实际验证

本轮仅使用本机缓存还原、隔离页面模型／配置／SQLite／NTFS测试数据；没有启动或停止现有用户程序，没有操作正式挂载、历史或远端。证据位于artifacts/validation，重叠验证不相加。

| 范围 | 实际结果与证据 |
| --- | --- |
| 最小WinUI | x64 Debug非打包成功；首轮两条WMC1506警告随后修正：0092-ui-hints-minimal.log |
| 最终Debug／Release | 两种配置的x64非打包WinUI及原生扩展编译成功，最终日志无警告／错误：0092-ui-hints-Debug-build.log、0092-ui-hints-Release-build.log |
| 相关回归 | 两配置各96/96、零失败／跳过；覆盖PageViewModelTests、AppearanceSettingsTests、NavigationTests、ConfigurationBackupTests、TaskHistoryClearTests和CompletedTaskHistoryTests。证据0092-ui-hints-Debug-tests.log／.trx、0092-ui-hints-Release-tests.log／.trx、0092-ui-hints-results.json |
| 结构与保全 | 8个XAML的Header、PlaceholderText、Tooltip、无障碍名称及操作事件逐项与本轮前一致；挂载卡片和337个无关文件摘要、暂存内容保持。主工程167个Compile无重复，TextVisibility一次。证据0092-ui-hints-baseline.json、0092-ui-hints-structure-audit.json、0092-ui-hints-compile-items.json |
| 生成绑定核对 | 核对两个协议窗口生成的.g.cs：测试／错误IsOpen和状态Text使用DP变更回调；备份反馈IsOpen与密码区域Visibility亦随DP更新。属于生成代码静态核对，不是GUI运行验证 |

不可变任务结果的Error／Message最初使用OneWay可见性绑定，最小编译提示WMC1506。改为与原任务快照相同的一次性绑定，重建后警告消除；ViewModel和可变备份行继续观察原属性通知。未为纯文案删除新增镜像测试，使用现有相关回归。

### 待测试与产物边界

- 实际WinUI下五页和配置／备份窗口的布局、窄窗／高DPI、空反馈折叠、悬浮说明，以及校验错误、保存结果、连接测试和解密预览切换尚未执行。
- 本轮完成非打包编译，未启动非打包进程；ARM64、MSIX重新生成／签名、安装升级及Explorer未执行，不能沿用前批产物或501/501全量结果作为本次追加通过证据。
- 所有产品非二进制修改先生成补丁，再执行git apply --check和git apply；未更改安全设置。辅助脚本、日志及构建产物保持忽略；本版仍未暂存、未提交、未推送。

## 2026-11-05追加：myweb隐藏临时路径兼容

### 现场与根因

- 用户报告D:\myweb的EXX11901.shx、FSD.SHX和MON.SHX无法上传。只读SQLite核对三项确为Failed，错误为旧版通用“认证或权限不足”，不是仅凭Explorer箭头判断。FSD.SHX尚无远端回执，旧RAR的Canceled记录继续保留。
- 使用当前挂载凭据只读访问配置的HTTPS服务dav.dwzcna.site：根及4649.zip为207，精确日志中的隐藏探测路径为403，普通随机缺失路径及FSD.SHX为404。响应有界、证书验证及禁止重定向保持；该初始只读阶段未打印凭据或原始响应、未执行PUT／MOVE／DELETE；后续授权写入测试另列，始终未访问已取消的10.0.0.20:5007测试。
- 原能力探测生成.hybridmount-probe-随机GUID，创建前PROPFIND即被拒绝。相同拒绝规则的本地HTTP服务下，旧代码直接创建及MOVE回退2/2重现相同异常，堆栈确认失败发生在PrepareTemporaryRequestAsync，尚未发送用户文件正文。不能把这些403一律解释为密码错误。

### 已完成代码

- 新DAV能力探测使用hybridmount-probe-随机GUID，MOVE暂存使用hybridmount-upload-随机GUID；没有新增辅助目录。仍先写精确意图、确认缺失，所有条件PUT、Overwrite:F、锁、摘要与版本核对保持。
- 新旧普通／隐藏名称均可读取精确日志，恢复仍隔离挂载、远端根与账号，不能按前缀获得清理权限；未登记同名前缀内容、内容改变及目录继续保护。原五条隐藏CleanupPending记录未删除或假定不存在。
- 401明确提示认证失败，403明确提示路径或操作被服务器拒绝，HttpStatus及失败状态保持；没有忽略真实403或自动改用不安全覆盖。
- 既有DAV测试及历史验收恢复器兼容两种名称；新增6项有实际行为的回归。S3协议、数据库、挂载与凭据配置、任务重试规则、版本0.0.9.2以及本轮前未提交改动保持。

### 实际验证

| 范围 | 结果与证据 |
| --- | --- |
| 真实服务只读 | 上述207／403／404对照：0092-myweb-upload-diagnosis.json、0092-myweb-readonly-http.json；单独不算真实上传通过 |
| 授权实服写入与精确清理 | 唯一73字节文件：PUT 201、SHA256／强ETag匹配、重复条件创建412、条件DELETE 204、最终PROPFIND 404；0092-myweb-live-owned-write.json、0092-myweb-owned-metadata.json |
| 修复前复现 | 2/2按预期失败，堆栈指向临时存在性查询：0092-myweb-before-fix.log／.trx |
| 最小编译 | x64 Debug WinUI非打包成功：0092-myweb-minimal.log |
| 针对性 | 61/61，零失败／跳过：0092-myweb-targeted.log／.trx |
| 完整x64 | 最终Debug／Release各507/507，零失败／跳过：0092-myweb-Debug-full.log／.trx、0092-myweb-Release-full.log／.trx |
| Release编译 | x64 WinUI非打包成功：0092-myweb-Release-build.log |
| 变更保全 | 原无关源码与暂存摘要保持：0092-myweb-code-baseline.json、0092-myweb-results.json；未暂存／提交／推送 |

新增6例包含禁止隐藏路径时直接PUT及已验证MOVE新增、已有目标仍拒绝覆盖；新旧临时日志精确恢复且未登记同前缀文件保留；真实用户目标403仍失败；独立NTFS／CFAPI根内文件系统触发FSD.SHX新增、修改、同步标记及断开重连不重复发送。测试同步根使用夹具精确注销，不操作正式D:\myweb。

### 用户授权后的真实服务验证

- 首次执行被自动审批以缺少远端创建／删除授权拒绝，命令未启动。用户随后明确要求在此WebDAV根用一个唯一小文件验证写入并在摘要、强ETag匹配后精确删除；本次据此执行，不扩展到正式任务或旧临时项。
- 本轮只创建`hybridmount-probe-2fa434cae1fd4aef9747589feea4b41d`，内容73字节，先落盘测试意图，再PROPFIND确认404、`If-None-Match:*` PUT得到201。首两次GET均200但未取得可用强ETag，辅助测试按保护规则保留现场；没有另建文件或重发创建。之后同一文件GET、HEAD与PROPFIND的强ETag一致，读回字节及SHA256匹配；本地校验函数对强／弱标签的隔离自检正确，首次缺少验证器的原因未据此定论。
- 对同一文件重复条件PUT返回412，原内容不变。最后再次GET核对SHA256为`E404B135DC2665AF448DADD3BE4179C3015AC88A24E9838114B5683861A446F9`、强ETag为`"49-65f8797df9679"`，以该ETag发送`If-Match`条件DELETE得到204，再次PROPFIND为404。`CleanupVerified=true`，本轮远端文件已确认清理，首次暂停及恢复过程保留在同一报告，未丢弃失败证据。
- 仅访问当前配置HTTPS服务，凭据只在内存中处理，不打印Authorization、密码或原始响应。未建远端目录、未代为重试三个SHX、未清理五条旧隐藏记录、未改写正式挂载数据库或重启用户程序。此结果证明该服务普通路径的条件创建、读取与条件清理可用；没有把原始HTTP请求记为新版驱动／文件系统触发的正式上传通过。

### 未执行与后续边界
- 三个正式SHX未代为上传或重试；已运行的调试进程未被停止、重启或热替换。加载新构建后需显式重试Failed任务，并按实际服务结果核对；现阶段不宣称真实上传已恢复。
- 未重新生成本轮MSIX或执行ARM64、GUI及Explorer完整验收，旧包和历史结果不得作为本次修复证据。
- S3文件级显示另案保留：CFAPI InSync=1、Modified=0及上传Completed，与Shell状态10／512不一致；一次Shell状态通知未消除，尚无唯一根因，也没有通过本次DAV命名修复声称解决。
