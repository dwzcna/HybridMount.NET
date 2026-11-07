# 0.0.9.29：WebDAV 降级保护、配额诊断与 Explorer 空白状态修复

日期：2026-11-07。原开发及自动验证预算3–5小时，追加Explorer确定性崩溃的现场捕获、符号定位及精确修复；桌面和外部等待另计。保留三个工程、SDK、公共文件源接口、IPC v2、SQLite及配置／备份格式。完成后独立本地提交，不推送。

## 代码完成

- 新增协议无关的InvalidRemoteChangeResponseException。先区分HTTP拒绝、网络、超时、取消和令牌失效；成功读取的REPORT出现坏XML、非法href、必需属性缺失、重复条目、令牌或续批结构错误时，才使整个挂载停用RFC6578。失败页不提交检查点，立即从根目录逐目录Depth 1完整对账；不修正双斜杠地址或跟随跨服务器／带查询href。
- 降级原因和首次时间仅保存在挂载会话内存中。普通刷新、自动重试、改检测间隔及内部运行对象重建不清除，后续使用完整对账间隔。完整枚举失败保持降级，下轮从根重新完整抓取，不受旧LastFullUtc短路；失败范围不推断删除。本地修改及未知上传结果继续受保护。
- 显式断开重连、关闭后重开检测及新应用会话重新探测，包括持久化Full集合；重新采用RFC6578时从空令牌建立完整基线，原索引及待处理变更保留至对应基线完整。实际模式为“完整对账（RFC 6578 响应异常，本次已停用）”，完整诊断显示原因和时间，不增加常驻说明。
- RFC4331复用原容量设置与缓存。写操作明确HTTP507按DAV标准错误分别报告账户配额、服务器磁盘或通用存储不足，使同远端范围缓存失效并阻止迟到查询覆盖；拒绝的上传进入现有失败／同步问题入口、保留快照及基线，用户显式重试。无响应继续未知结果保护，不自动覆盖手动上限或开启自动识别。
- 从已有OPTIONS捕获Allow、Accept-Patch及sabredav-partialupdate通告，完整诊断只读快照，区分通告和实际未验证。上传仍用条件PUT，没有新增PATCH请求。RFC4709取消，没有davmount导入。
- Explorer问题属性清除真正移除本程序编号1，保留其他完整属性；不再写空文字和空图标。安全读取公开System.StorageProviderCustomStates序列化容器，通过系统反序列化器有界校验来源和向量，不创建会触发故障的状态描述。仅在原身份保护句柄内用官方API写入，写后重新核对，未知格式或不完整其他属性保留并报错。每次应用会话一次性复核旧Cleared记录，沿用路径预留及恢复日志，不改变上传结果或代次，不自动重发。

## Explorer 确凿根因与现场收尾

用户在D:\myweb的详细信息视图中显示“同步问题”列时，Explorer可重复崩溃。Windows事件为windows.storage.dll 10.0.26100.9711、0xC0000005、偏移0x1765c7；匹配PE时间戳0xb7083c6b及微软PDB，定位到CustomStatePropertyDescription::RuntimeClassInitialize，经CIconListControl加载图像列表访问空文字向量。

短时只读调试捕获确认：编号向量长度1，文字向量长度0，文字指针0。对应文件为`D:\myweb\管道特性表-2.xlsx`。旧清除调用向StorageProviderItemProperties.SetAsync提交Id=1、Value=""及IconResource=""，系统保留编号却丢弃空文字／图标，导致列处理器解引用缺失项。这是本次确定性故障证据，不能将其他历史Shell问题一并归因或记为修复。

隔离NTFS／CFAPI回归通过后，正式程序退出期间使用同一生产发布器只精确修复上述文件：前后编号由[1]变为空，文字及图标也为空；根及文件身份、长度1836018、LastWriteTime保持，USN／ChangeTime只发生元数据写入变化。没有正文读取、远端请求、正式任务记录写入、配置修改、同步根重建或工具发起的Explorer重启。后续观察戳吸收尝试因旧戳不匹配被保护条件拒绝，更新次数0。

用户随后自行构建启动0.0.9.29调试程序，实际运行模式Unpackaged、日志Ready。程序按既有ShellOriginalStamp恢复日志自然收尾；只读核对上传539c9fa64d304a2aa3e9a0ac22ef46e9仍Completed、Generation=1，ShellState=Cleared、ShellError=null，恢复日志已清空。用户随后明确回复“已经恢复正常”。工具未代为重试该上传或强杀正式程序。

证据：`00929-explorer-events.json`、`00929-explorer-fault.json`／dmp、`00929-custom-states.json`、`current-00929-blank-repair.json`及`00929-live-repair-after-start.json`，均位于artifacts/validation，转储和辅助诊断程序不进入产品提交。

## 实际自动验证

| 项目 | 实际结果 | artifacts/validation证据 |
| --- | --- | --- |
| 最小x64 Debug主程序 | 独立Unpackaged输出构建通过 | 00929-debug-complete.log |
| 最后Shell补丁针对性回归 | 23/23，失败及跳过0 | 00929-shell-final.log／trx |
| x64 Debug全量 | 1138/1138，失败及跳过0 | 00929-debug-complete.log／trx |
| x64 Release全量 | 1138/1138，失败及跳过0 | 00929-release-complete.log／trx |
| x64／ARM64签名MSIX | 签名、版本、架构、内容及CloudFiles注册核验通过 | 00929-release-complete.log、00929-arm64-complete.log |
| ARM64测试 | 仅交叉编译通过 | 00929-arm64-tests-complete.log |
| x64非打包Release | 构建通过，未正常启动最终输出 | 00929-unpackaged-complete.log |
| 实际Compile登记 | 主193／测试261；挂载源码133项两者各一次，无重复遗漏 | 00929-compile-main.json、00929-compile-tests.json、00929-compile-check.json |

本版新增42项回归。降级覆盖初始／增量REPORT、坏XML、双斜杠／跨服务器／查询href、属性缺失／属性404、重复路径及循环令牌、子集合出错后从根完整抓取、新目录与其他挂载隔离、HTTP拒绝、正文超时／取消、全量失败保护及重试、旧Full检查点重探测、关闭／重开和重启。配额覆盖两种标准507、外来命名空间、损坏及空错误正文、无自动重发、显式重试、快照和问题保留、缓存租约内失效与迟到查询、PATCH通告组合及诊断零额外请求；原容量回归继续覆盖合法／缺失／非法／溢出等边界。

Shell新增实际向量移除和对齐断言，4项新测试覆盖仅拥有的坏编号、其他完整属性保全、真实发布／清除与应用重启修复。全量继续覆盖普通本地文件、占位、固定、身份和正文保护、取消、上传恢复及读取；不以自动测试通过替代Explorer所有视图的目视验收。此前Shell修复前1134项结果为中间证据，本表采用最终1138项结果。

## 当前真实 WebDAV 只读结果

2026-11-07 12:17（UTC+08），使用当前生产驱动和隔离SQLite索引，对D:\myweb对应HTTPS端点只允许OPTIONS／PROPFIND／REPORT。共6个请求，其中空令牌初始REPORT一次、带令牌增量REPORT一次；返回有效响应，5个条目、1个集合，增量后待处理数量保持5，没有重复生成变更。能力模式为RFC6578增量检测，本轮未触发降级。

服务工单修复后的响应已有效；历史坏REPORT证据单独保留，故本版降级由模拟坏响应回归验证，不能将其说成当前真实服务仍坏。实测配额已用5537792、可用10731880448、总计10737418240字节（10 GiB）。OPTIONS的Allow没有PATCH、没有Accept-Patch，DAV仍通告sabredav-partialupdate；不推定局部上传可用。

本次远端写入0、正式挂载根访问0、正式定义保持一致，不装配正式CFAPI或运行仓库，不访问已取消NAS。证据入口：`current-00929-live.json`所指report.json，以及`00929-live-incremental.log`；不保存凭据、Authorization或sync-token。

## 产物

| 架构 | 签名MSIX | SHA256 |
| --- | --- | --- |
| x64 | artifacts/packages/x64/HybridMount_0.0.9.29_x64_Test/HybridMount_0.0.9.29_x64.msix | 3DBEEBEFE20F451080A8DBCE6E04FD8216CA974D33D54A1D1D6F668E207A1000 |
| ARM64 | artifacts/packages/ARM64/HybridMount_0.0.9.29_arm64_Test/HybridMount_0.0.9.29_arm64.msix | CBD0D1F8D8B9C1653A8E185F65D2A67CD68AACAB49D4A7F2FE5D644AAA460FC5 |

机器可读摘要：`artifacts/validation/00929-evidence.json`。版本、主程序、扩展、测试及MSIX清单统一0.0.9.29。本轮所有产品／文档文本通过版本补丁检查后应用，辅助程序、转储、凭据和本机AGENTS不进入提交。

## 待测与交付边界

用户已确认本次详细信息视图的崩溃恢复；其他完整GUI交互、Explorer视图／属性组合及长期运行矩阵、最终Release非打包正常启动、MSIX安装升级和ARM64实机仍待测。用户自行启动的Unpackaged Debug已实际Ready，但不能代替最终Release和MSIX安装验收。没有安装包、注销或重建正式同步根、强杀程序或主动重启Explorer。历史SHELL-01与F04、F08–F11等原待办保持单列，不因本轮全量通过而改记修复。

独立本地提交，不推送GitHub或Azure。
