# 0.0.12.0 验证与交付记录

## 范围和状态

基线master a95d540（0.0.11.0），统一版本0.0.12.0。四工程、当时的预览身份安装身份、公共IPC v2、配置和SQLite表结构保持；本地任务JSON仅追加可选诊断，公共任务响应清除这些本地字段。所有产品文本经0.0.12.0版本化补丁检查后应用，证据副本位于artifacts/validation/00120-work/，已应用工作补丁删除。

代码已完成：单文件固定取消与资源收尾、释放任务和恢复、前后台需求与通知分离、页头原生模板及溢出生命周期。以下结果对应本轮实际执行；末次局部修改按受影响范围复测，不把较早主矩阵当作追加修改后的完整重跑。用户追加的旧排除规则enabled异常也已修复：先消费合法旧布尔字段，再按原严格选项解析当前规则，旧停用项不启用、其他未知字段仍拒绝。

## 已执行阶段回归

| 证据 | 结果 | 覆盖 |
|---|---|---|
| managed-priority-Debug.trx | 48/48 | 固定意图、取消、释放及托管优先级 |
| integrated-Debug.trx | 91/91 | 原生保留视图、延后通知及相关集成 |
| lifecycle-review-Debug.trx | 53/53 | 程序固定任务取消、历史清理与生命周期 |
| bounds-review-Debug.trx | 26/26 | 128项饱和插队／回填、旧取消、新版本恢复、八视图淘汰 |
| release-parent-filter-Debug.trx | 12/12 | 真实锁定释放、批次仅汇总释放子项、取消和前后台消费者 |

中间失败保留，不将后续测试通过推断为真实Explorer或ARM64实机通过。原生测试映像及根均使用隔离管道／CLSID／GUID路径，不操作D:\myweb或其他正式根。

## 交付矩阵

| 项目／证据 | 实际结果 |
|---|---|
| x64 Debug主矩阵 delivery-Debug.trx | 337/337，0失败／0跳过 |
| x64 Release主矩阵 delivery-Release-2.trx | 337/337，0失败／0跳过 |
| 末次通知饱和／日志去重／浏览与取消边界 final-boundary-Debug／Release.trx | 两配置各69/69，0失败／0跳过 |
| 用户追加enabled兼容、配置／备份往返及排除流程 legacy-enabled-Debug／Release.trx | 两配置各108/108，含8项严格旧字段正反用例；0失败／0跳过 |
| x64 Debug／Release非打包 | 最新源码构建通过 |
| x64／ARM64 Release及签名MSIX | 两架构构建、签名和包检查通过；1073资源键×3语言、8原生字符串×3，实际PRI及原生入口完整性通过 |
| ARM64测试程序集 cross-tests-ARM64-Release.log | 交叉编译0警告／0错误，未执行ARM64测试 |
| 独立WinUI ui/.../smoke-data/result.txt | 任务／日志两页×三语言×明暗×520／820／1050像素，共36组原生模板、可见溢出、反复开关、缩放重排关闭及卸载页面关闭通过 |
| 独立MSIX menu-wildcard-bc0120a7/menu-registration.json | 七类IShellFolder/IContextMenu查询、冷文件身份／Pin／InSync／本地大小保护及清理通过；无远端源 |

各TRX范围有重复，不相加冒充独立用例总数。337项主矩阵先于末次预览通知饱和保护和enabled追加；对应修改分别以69项和108项双配置定向复验。Release首轮新增日志断言被xUnit2031分析规则阻止编译，改用Assert.Single谓词重载后通过；没有降低分析规则。证据Release-before-analyzer-fix.log保留。

末次通知回归将消费者阻塞，向256项有界队列注入512条说明消息，再更新最终空需求；消费者每次唤醒读取所有根的最新完整快照，唤醒项被挤掉仍交付最终撤销，避免保留已关闭视图需求。查询仍不生成或读取源。

独立测试包名称当时的预览包身份.Probebc0120a7w，版本0.0.12.0，独立CLSID；测试包已精确卸载，GUID根及测试跟踪键已清理。七类传统菜单中ProductEntries均为0，未验证自有现代菜单覆盖；不改变生产菜单注册以制造通过。上述IShellFolder计时含系统／第三方菜单，不作为本产品Shell回调或Explorer消息性能结论。UI截图来自主内容RenderTargetBitmap，不包含桌面合成阴影，阴影实际显示仍待人工。

生产包未安装，使用既有CN=dwzcn证书签名，不新增证书或修改信任，签名验证0警告／0错误，未附时间戳。产物与SHA-256：

- x64：`artifacts/packages/x64/HybridMount.NET_0.0.12.0_x64_Test/HybridMount.NET_0.0.12.0_x64.msix`，SHA-256 `113796E78FF14F5A9C8EEBA922293DEA927FDD2E60E3A6FDD96DA585B442B19B`。
- ARM64：`artifacts/packages/ARM64/HybridMount.NET_0.0.12.0_ARM64_Test/HybridMount.NET_0.0.12.0_ARM64.msix`，SHA-256 `86A37B85343ADD015754F8BA2A538749E7FCC6F2D220C4FE5B19BEC9F5C08E27`。

完整TRX计数、产物哈希、资源数量、UI及独立包清理快照见artifacts/validation/00120-work/build-delivery-summary.json。资源／工程XML、JSON和Git差异检查通过；清单版本由Directory.Build.props驱动构建同步，生成差异也经版本化补丁重新核验应用。

## 失败定位与复验

- 初轮释放边界发现零字节文件OnDiskSize为0并不证明已经脱水，另发现0x80070322属于oplock暂时竞争；均保留保护并归为暂缓。
- 扩大Debug矩阵首次335/337，后续336/337。显式释放原先在同根执行器忙时提前返回；修复为根锁外等待同一执行器。发布前增加实际范围核验。
- 进一步记录发布范围定位剩余失败：父批次聚合误将同路径keepOnDevice完成记录当作freeSpace子项；仅筛选freeSpace后，原失败及新增先固定再带锁释放12项通过。release-publication-Debug.trx保留暂缓时仍完整本地的证据，不将其误归因于第三方重新水合。
- 独立WinUI宿主第一轮不能直接RenderTargetBitmap绘制Popup，第二轮当前会话无法GDI屏幕截取，第三轮桌面将1250像素请求限制为1100；都属于探针条件失败，未改产品以迎合测试，原partial／failure保存。改为实际可用宽度及主内容渲染，弹层几何与状态另外检查；不据主内容图片宣称阴影视觉通过。

## 生产日志只读分析

用户提供Logs (1).zip，原始文件不修改，安全解压到production-log-analysis/。包含0.0.11.0 Packaged x64的diagnostics.db（70条）与生命周期记录，没有挂载索引／任务数据库。

- 2026-11-12 03:50:56—04:04:31 UTC（本地11:50:56—12:04:31），记录14—40共27次同一EntryId关联的“释放请求暂缓”，均为0x80070020，约30秒间隔；没有最终释放成功证据。
- 04:04:36和04:05:54 UTC，程序两次报告26032项目手阀采购提资表-20261112.xlsx“移出同步根被拒”，CFAPI type=11随后返回0x80004005。日志没有完整目标路径和成功回执，不能仅据此判断用户实际目标，也不能证明文件已改名为20261113.xlsx。
- 0x80070020表示共享冲突。0.0.11.0 CompletePendingUnpinsAsync → CloudMountSession.CompletePendingUnpin → 原生dehydratePending取得独占Cloud Files句柄并脱水；每30秒补查与日志一致。0.0.12.0同一路径增加可见暂缓任务、状态唤醒和精确核验，不能替其他进程解除占用。
- 日志关联值是EntryId而非文件路径；缺少条目索引，无法证明27条释放记录就是该XLSX。没有现场句柄／系统跟踪，也无法识别占用进程。生产与D:\myweb同挂载关系采用用户说明，本轮未扫描正式目录或读取正文来补证。

## 待人工和设备验收

真实Explorer前台／多窗口标签页／导航关闭、前台插队、后台不UIA与重获焦点刷新；本机主程序退出后本地按需取图与通知收尾；取消慢响应／等待执行位／完成边界、独立外部消费者、重新固定和断开重连；权限失败、占用解除自动继续、生产重命名及锁定者；菜单反复展开、明暗／高对比度、DPI及键盘交互；生产Win11 21H2、DSM和ARM64实机。

暖态Shell P95≤50ms／P99≤150ms、Explorer消息P95≤200ms及冷／热启动指针与上传准备／网络／提交对照尚需实窗测量。微软STARTUPINFO反馈说明作为参考，未把刚挂载短暂busy当作已确认传输缺陷或承诺可消除的行为：https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/ns-processthreadsapi-startupinfow 。菜单默认样式修复参考已确认案例：https://github.com/microsoft/microsoft-ui-xaml/issues/9834 。参考资料不作为测试通过证据。

未安装生产包、未操作正式挂载或重启Explorer，未补跑.33停止专项；保留用户未跟踪文件。按用户后续指令本地提交，不推送。
