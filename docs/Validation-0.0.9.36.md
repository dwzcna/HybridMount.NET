# HybridMount 0.0.9.36 验收记录

日期：2026-11-08。Windows x64、.NET 10.0.401、Visual Studio 2026、Windows SDK 26100。代码及本轮自动验证完成；GUI和实机单列。0.0.9.35已先独立提交2be07ed；本版独立本地提交，不推送。原预计开发及自动验证6—10小时，桌面等待另计。

## 实现边界

- 两个状态资源采用ICO，各含16、20、24、32、40、48、64、128、256独立PNG编码帧，嵌入HybridMount.Ext.dll的102、103图标资源。系统IconResource引用当前部署路径DLL,-ID，读资源表不加载COM。保留PNG兼容和完整属性向量写入，不恢复暂停的菜单或缩略图。
- 任务页从持久upload／remote_pending聚合冲突，比较仅查元数据，不水合或用大小时间宣称内容相同。明确使用本地和使用远端两种实际取舍，不映射成普通重试或取消后刷新。
- 本地选择建立新代次、冻结快照和确认的远端条件版本，交还现有容量／条件写入／未知结果保护。远端再次变化返回冲突，不自动续授权。
- 远端选择先备份原本地完整正文，摘要、长度及恢复目录均核验，再下载固定远端版本；受保护Win32句柄用低位独立事件写入，避免重新绑定CFAPI完成端口；验证真实新正文后才设置InSync并事务提交映射、基线及完成状态，保留文件身份和Pin。普通文件只因真实内容替换转换占位，不为显示转换。
- 可选JSON记录Preparing／Prepared／Committing与备份路径。失败或重启后保持ResolutionPending，扫描、自动远端应用、普通重试／取消均不能越过屏障；再次明确远端选择可恢复，原始及后来备份均保留。不会自动上传部分替换的正文。
- 仅支持同路径完整文件的可靠取舍；目录／类型互换、部分正文、未知结果、活动读取、外部链接／多硬链接或新事件／相关任务变化均拒绝。备份位于本模式数据目录Mounting/ConflictRecovery，永不被缓存回收自动删除，路径在反馈、任务结果和诊断可查。
- 保留三个工程、现有SDK、SQLite表结构、文件源接口、IPC v2、配置与备份格式。所有目标文本经patch/受检补丁后应用；辅助脚本／日志／产物保持忽略。

## 已执行

- 最小x64 Debug非打包WinUI与原生构建：artifacts/validation/00936-icons-debug-build.log、00936-ui-build.log。
- ICO资源／隔离CFAPI属性验证26/26：00936-icons/00936-icons.trx，包含独立帧、真实LoadLibraryEx资源映射及LoadImage提取／GDI释放，不创建COM对象。
- 冲突／VM／副本／问题相关78/78：00936-conflicts4/00936-conflicts4.trx。
- 最终新增专项23/23：00936-final-focused/00936-final-focused.trx，包括WebDAV/S3两种取舍、精确事件退役、发送前版本变化、本地编辑、备份损坏、取消、中断重启恢复、固定占位、零字节／700000字节正文、多硬链接保护及原生ICO资源。
- 初始回归编译夹具Scope字段错误保留00936-conflicts-tests.log；首轮13项中8通过／5失败，发现普通文件错误调用CFAPI范围查询。已在普通文件分支修正，后续13/13、78/78和最终23/23通过。原始记录保留，不把首轮写为通过。

## 最终自动验证

- x64 Debug广泛1222/1222：00936-debug-regression/00936-debug-regression.trx。其后针对对话框所有权及原始备份阶段补充回归，最终48/48：00936-resolution-final/00936-resolution-final.trx；最终Release广泛1223/1223：00936-release-regression/00936-release-regression.trx。
- 对话框首次跟进46项中45通过／1失败，原因是硬链接夹具建链后元数据事件改变了手工冲突状态；补齐扫描稳定步骤后46/46，最终48/48及Release广泛通过。原始00936-dialog-final.log与复测均保留，不删除失败证据。
- 最终x64 Debug／Release WinUI、Release非打包、x64／ARM64签名MSIX、ARM64测试交叉编译通过：00936-delivery-debug.log、00936-delivery-x64.log、00936-delivery-arm64.log、00936-delivery-unpackaged.log、00936-delivery-arm64-tests.log。全部0警告／0错误，签名及包内容检查通过；不等于安装／实际启动验收。
- ARM64 DLL仅作为资源映射，在x64进程提取102／103的16、32、64、256图标共8次通过，未执行ARM64代码：00936-arm64-resource-audit.json。
- Compile登记：主218、测试296，无重复或共享源遗漏：00936-final-compile-audit.json。独立非打包PowerShell只读核验测试根引用0：00936-final-root-audit.json，既有正式记录保持。
- 广泛回归继续排除用户已停止的0.0.9.33类型：FailedMountTests、OrphanedMountRecoveryTests、ClearRemovalTests；不是宣称这些旧专项通过。使用最多4线程的00935-bounded.runsettings，不与其他重构建同时运行。
- 最后只在完整备份及当前正文验证通过、即将写入时记录Committing；准备失败仍为Prepared。中断后再次使用远端会保留并继续展示最初完整本地备份，而非用中间正文备份替代原始恢复位置。

## 产物

- x64：artifacts/packages/x64/HybridMount_0.0.9.36_x64_Test/HybridMount_0.0.9.36_x64.msix；SHA256 `2E9601B408238748FB814999CE43161D9FFCE1C6508B6DCA8CBDA96FCB43FCD7`。
- ARM64：artifacts/packages/ARM64/HybridMount_0.0.9.36_arm64_Test/HybridMount_0.0.9.36_arm64.msix；SHA256 `1B0A7E81FCECEB92486F3839131D81B90ED7AAB30338CDBA02CD2BC6A6E1CC20`。

## 待测试

- Explorer实际显示已排除／同步问题两图标，以及DPI、明暗主题、升级属性重发；提取成功不等于Explorer自动渲染通过。
- 任务页冲突筛选／路径定位、对话框两端比较与窄窗布局、两个取舍及备份打开／手工恢复、连续点击、页面切换、断开退出。
- 正式MSIX安装升级、非打包实际启动及两个命名／磁盘入口目视、ARM64实机。
- 真实提供商兼容性及超大真实文件人工流程。本轮仅隔离NTFS/CFAPI和回环DAV/S3，不写正式服务、不停止正式进程、不主动重启Explorer或重建正式根。
- 用户已明确后续无法响应，以上人工项目保留待测，不等待响应，不记为通过。0.0.9.33专项不继续。
