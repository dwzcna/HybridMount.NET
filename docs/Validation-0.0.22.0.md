# 0.0.22.0 验收记录

## 实施范围

四工程目录与依赖规整、模型展示拆分、仓库／日志／设置／认证／调度契约、NativeShared同源编译、测试夹具和分组、构建结构门禁以及根AGENTS。实现与实际测试分开记录，未以改名或构建次数冒充行为验收。

外部配置根、SQLite schema8、.hmxbk编码和加密、公共IPC v2、包身份、COM导出和输出名称保持。原TaskListItem排版及用户未跟踪的Plan-0.0.20.3.md保留，不纳入本版业务提交。不推送、不安装生产包、不操作正式挂载、不重启Explorer。

证据目录：`artifacts/validation/00220-work/`。范围计划、当前结构和测试删并映射分别见Plan-0.0.22.0.md、SourceStructure-0.0.22.0.md及TestConsolidation-0.0.22.0.md。

## 迁移前基线

在移动源码前，用已构建的0.0.21.0 Release程序集运行原完整门禁：2151通过／2158总数、7失败、0跳过，21分45秒。`baseline-release.log`／`.trx`完整保留。七项为DAV清理读次数、旧模型结构断言、真实TCP共有期限启动竞争、P1上传类型变化恢复、10000项任务快照性能、群晖取消队列、容量跨根异常类型。不能将它们记为本版新回归，也未直接忽略。

## 分批实际验证

| 阶段 | 结果 | 证据 |
|---|---|---|
| A：目录／编译／NativeShared | 主程序及测试最小x64 Debug通过；两原生工程通过 | phase-a-debug-final.log、phase-a-tests-final.log、phase-a-native-retry.log |
| B：仓库契约 | 133/133 | phase-b-debug-final-regression.log／phase-b-debug.trx |
| C：模型渲染 | 最终110/110 | phase-c-debug-final-regression.log／同名trx |
| D／E | 主程序各0警告0错误；后续组合覆盖调度与认证 | phase-d-debug-build.log、phase-e-debug-build.log |
| F—H：日志／设置／快照／共享夹具 | 修正后114/114 | phase-fgh-debug-fixed-regression.log／同名trx |
| I：纯Helpers／结构规则 | 94通过／95总数；剩余为基线DAV清理读次数，随后更正 | phase-i-debug-fixed-regression.log／trx |
| 最终Debug组合 | **511/511**；最小非打包及隔离原生构建通过 | debug-final-targeted-build.log、debug-final-511.trx |

这些集合有重叠，不能相加当作独立场景数。

## 过程失败及修正

- 初次原生迁移缺少共享ThirdParty头路径，随后将原头及版权同迁；原生MUI存在既有覆盖警告。
- 提取PageMessageSeverity后，三个旧生成XAML文件仍引用旧命名空间；仅删除对应过期生成文件并重生成，没有清空整个缓存。
- 仓库契约初轮修正了重复声明、Action<Guid>事件和private setter边界，原事务及实现可见性保持。
- 资源拆分首轮遗留页路径，以及模板在传参前Render造成FormatException，分别更新实际路径与将参数一并交给消息渲染。完整门禁进一步发现SourcePermissionException中的同类遗漏，单独留存失败和修正后证据。
- 若本机WinRT根枚举明确返回COMException，根诊断的安全性回归分别处理查询成功／失败；不把查询失败冒称缩略图不可用，原身份、Pin、InSync、磁盘占用及零源读取保护继续断言。
- 旧“同文件／默认构造／内部字段名”等四项检查由集中门禁、实际生产编译及保留的行为测试替代，逐项替代映射另存。
- DAV清理现有额外权限核验带来第五次元数据观察，保留有界强标签重试、摘要和条件删除。跨根异常按现有WebDavRedirectException断言。共有70ms期限改为可取消HTTP适配器，不调整请求次数上限；真实服务重定向覆盖保留。
- 两次辅助文件写入遇到瞬时PermissionError，复核非只读后有界重试完成；未改ACL或用户数据。

## 最终交付

原22.0完整功能Release为2152/2162、10失败，未执行后续性能和签名；修正权限模板及测试隔离后，原生回退的前序21.1完整功能Release为2163/2165、2失败（Shell状态发布取消、回环PUT连接超时），不能作为本版完整门禁通过。原始TRX分别保存在00220-work/release-first-gate.trx及00211-work/release-final-2165.trx。

按用户最新指令，先恢复并提交本版四工程整理，再恢复缓存继续22.1。恢复后结构门禁686个C#源及NativeShared登记通过；x64 Debug主程序、Ext、Stock及三组隔离原生映像构建成功，定向189/189通过，包含架构、日志边界、WebDAV重定向及TLS、备份、Shell状态、原生握手／传输、MUI与导航。主程序及生产／隔离原生模块的FileVersion直接核验均0.0.22.0。证据：restored-debug-build-test.log、restored-debug-189.trx、restored-00220-binary-versions.json。

原生MUI保留既有覆盖警告。本版恢复后未重跑完整Release，也未执行本版双架构签名包或设备测试；按最新顺序先本地提交，再恢复缓存继续22.1的直接产物验收。

## 规模与待测边界

按同一自有源码后缀口径，不计第三方头和生成目录：主工程393文件／32736行→487／34937，测试206／22723→214／23666；原生原58文件拆为Ext14、Stock35、NativeShared10。行数增加主要为显式using、接口、展示边界和文件拆分，没有追求机械删减比例。原始清单及分类在inventory-before.json、inventory-after-summary.json；八个共享夹具各用例保持独立实例。

待测：本版完整产品鼠标／键盘和系统选择器、实际WinUI／Explorer视觉、VS现场密度中断、跨屏DPI、高对比度、真实AWS／DSM／WebDAV／代理、Windows11 21H2及ARM64实机。以前版本的隔离UI结果不是本版验收；ARM64交叉编译也不是设备测试。
