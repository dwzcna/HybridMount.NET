# HybridMount 0.0.9.37 验收记录

日期：2026-11-08。Windows x64，.NET SDK 10.0.401、Visual Studio 2026、Windows SDK 26100。0.0.9.36已先本地提交7a83e4d，本版随后实施。原预计代码与自动验证11—18小时，工具／安装／桌面等待另计；不推送。

## 当前结论

SQLite缩略图缓存、本地生成与独立COM宿主代码已实现；**Explorer自动接管未完成，正式包不启用缩略图注册**。SDK合法候选在实际MSIX中破坏原生菜单，自动选择也失败。缓存／显式COM成功与正式自动显示分别记录，不能用前者替代后者。

正式源清单保持空CloudFiles和无自有COM／菜单，保留应用、启动任务及ShellMaintenance。构建拒绝实验参数；未注册候选保存在Packaging/CloudThumbnails.candidate.xml。开关隐藏、原偏好与备份字段保留，不后台预览、不接管正式根、不改变水合策略或列布局。

## 实现边界

- 固定物理路径%LOCALAPPDATA%/HybridMount.NET/Mounting/Thumbnails/thumbnails.db。图片以32／64／128／256档PNG BLOB保存，只产生DB／WAL／SHM；旧PreviewCache保留但不再读取或写入，不批量迁移／删除旧文件。仅该子树排除MSIX文件系统虚拟化。
- 独立缓存schema1，roots／bindings／images与产品SQLite索引完全分开。当前SID、根FileId、路径、配置摘要、启用状态及epoch控制授权；文件FileId、USN、长度、时间、内容版本及CFAPI身份防止重用旧图。不保存凭据或源地址明文。
- 命中只读、不建库或修复schema、不更新LRU。损坏／未知版本／忙库返回miss；写入短事务、跨进程命名锁和WAL，失效／精确根清理不修改其他挂载。回收经SQLite正常检查点，不手工删除活动WAL。
- 在线仅只读现有有效BLOB，miss不联网／水合；本地完整内容才进入既有隔离解码器。生产禁用旧远端预览，测试显式开关仍可回归旧协议。编解码器有512MiB Job、10秒、8MiB／32窗读取边界；HEIC／WebP等依赖系统已安装的WIC编解码器，不承诺任意真实媒体可解码。
- 独立STA COM宿主复用主EXE及原生DLL，回调仅读固定DB；不创建WinUI、托盘、挂载、凭据或协议服务。缓存miss／坏PNG返回失败和空位图，成功按请求缩小、不放大。工厂撤销前等待对象／锁／活动回调结束。
- 新requestLocalPreview沿用IPC v2；缓存miss仅向已运行、同SID／会话及同映像进程投递有界请求，不发送旧requestPreview、不启动主程序。纯非打包无受支持云绑定，不能借用另一个包的注册。
- 关闭／移除重读当前配置并递增epoch，旧工作不得重新启用；解码后先释放正文保护引用再等待根锁，移除在锁外先取消并等预览结束。固定／脱水只重绑已确认内容，释放任务先完成重绑再发布Completed，Shell通知仍在根锁外。读取器支持断开后仍存在的持久CFAPI信息，不转换占位或强改InSync。

## 阶段自动验证

- 最小x64及多轮WinUI非打包编译：00937-cache-main-build.log、00937-cache-integration-build.log、00937-native-server-build.log、00937-host-build.log等。
- 数据库首批6/6：00937-cache-store2/00937-cache-store2.trx；首次5/6因夹具SQLite池保留临时文件，改Pooling=false后通过。缓存／解码／CFAPI相关21/21：00937-cache-pipeline。
- 本地生成／独立读取／能力整合26/26：00937-local-cache3；首次25/26发现断开后属性tag=0但CfGetPlaceholderInfo仍成功，读取器改用经根核验后的持久元数据查询，未改变其他Inspect语义。原始00937-offline-reader.log保留。
- 独立原生LocalServer4/4：00937-native-host，覆盖缩放、miss／坏PNG、持有对象空闲不退出、停止期间回调生存期及零水合。使用独立CLSID d0037037和DLL，不注册正式类。
- 后续缓存／能力17/17：00937-store-final，包含精确根清理、并发读写与epoch屏障。
- 第一轮综合65/69：旧ViewModel测试仍允许无包绑定、旧原生夹具仍期待requestPreview，已分别更新为实际打包能力和本地请求契约；另两项超时已真实复现，不能归为偶发。00937-focused-repair为2/4，00937-timeout-evidence及00937-stack-pair均0/2，原始日志保留。
- 00937-pair-stacks.log的托管栈确认：上传／清空在持根锁时等待原生oplock，预览又带正文保护引用等待根锁。修正解码释放顺序及移除取消顺序后，原组合00937-oplock-repair为2/2，最后00937-focused-pass2为69/69。中间68/69另发现释放任务先发布Completed、后重绑缓存的可见窗口；已将重绑移至完成发布前，保留该次失败证据。

## 真实隔离MSIX证据

所有包名为本轮随机HybridMount.ThumbnailProbe.<tag>，根位于artifacts/validation/00937-msix-<tag>/root-<GUID>，仅回环DAV。未替换正式产品包、未连接正式服务或重建正式根。

- c34cfb04、142764c5：包内外数据库路径与FileId一致，独立非打包进程命中在线仅缓存。五节点候选原生菜单缺失；自动图像工厂返回0x8004B200／0x8004B207。后者在SDK thumbcache.h定义为WTS_E_NOSTORAGEPROVIDERTHUMBNAILHANDLER，不能当作PNG解码或DB未命中。
- 7894aab8：空CloudFiles对照恢复包内外原生固定／释放菜单。首轮完整事实对比因Shell延后刷新ChangeTime失败，实际USN／内容／身份／Pin／InSync／缓存范围保持；之后缓存读取器只在干净内容且可靠USN一致时允许该差异，修改正文仍要求精确戳。
- 7f221d33：仅Thumbnail子节点在MakeAppx /nv下仍被XSD拒绝；未部署，不将绕过参数当验证通过。正式打包不用/nv。
- 59ae5dad：两个合法CloudFiles声明恢复菜单，自动缩略图仍返回0x8004B207。显式COM最初0x80080005，发现测试relay缺少产品已有的STA入口，修正夹具；不归因为产品COM实现失败。
- 2f9137d4：修正STA后的最终五节点对照，包外显式COM成功返回HBITMAP及ARGB，宿主读取同一物理DB；自动选择仍失败，原生菜单仍缺失，源请求12→12，文件身份／USN／大小／修改时间／Pin／InSync／水合范围保持。该候选明确未通过，不进入正式包。
- 8d00233f：最终空声明门槛通过，result.json的Success及Cleanup均true。包内与独立非打包进程保留原生固定／释放菜单，读取同一物理DB/FileId，在线仅缓存命中且源请求12→12。身份、大小、修改时间、Pin／InSync及水合范围保持；原始USN／ChangeTime独立留证，不以Shell访问元数据变化冒充内容更改。00937-empty-final3.log保留完整结果。
- 最终空声明的前两轮未通过：00937-empty-final.log使用了USN完全相等的过严断言；00937-empty-final2.log在证据文件尚未关闭时读取，已改临时文件写完后原子重命名。它们是夹具／元数据断言失败，不记录为产品自动缩略图通过。
- 早期406d8878因夹具包含不支持预览的普通条目中止；b0f2f154／92d1fe3f遇到WinRT注册0x80070490。日志全部保留，未将它们记为通过。result.json的Cleanup只代表夹具的原生根收尾，后续独立注册表审计仍发现32位残值及两项半成品注册，精确收尾另列。

## 最终验证与产物

所有证据位于artifacts/validation。产品源未在最终回归后继续修改；最后仅更新文档与只读／清理辅助脚本。

| 实际项目 | 结果 | 证据 |
| --- | --- | --- |
| x64 Debug广泛回归 | 1245/1245；失败／跳过0 | 00937-debug-regression/00937-debug-regression.trx |
| x64 Release广泛回归 | 1245/1245；失败／跳过0 | 00937-release-regression/00937-release-regression.trx |
| 最后针对性回归 | 69/69 | 00937-focused-pass2/00937-focused-pass2.trx |
| 最后x64 Debug及Release非打包构建 | 通过 | 00937-final-debug-build2.log、00937-final-unpackaged-release.log |
| x64／ARM64 Release MSIX构建、签名及内容核验 | 通过，均为空CloudFiles、无自有COM／菜单 | 00937-final-x64-package.log、00937-final-arm64-package.log |
| ARM64测试工程 | 交叉编译通过；没有ARM64测试运行 | 00937-final-arm64-tests-build.log |
| 实际Compile登记 | 主223／测试305，无重复或共享源遗漏 | 00937-final-compile-audit.json |
| 实验开关／直接属性true | 均按预期拒绝，不可绕过门控 | 00937-build-guard-experimental.log、00937-build-guard-property.log |
| Release真实EXE独立早期角色 | --thumbnail-server约32秒自然退出0 | 00937-early-role-smoke.json |

早期COM角色只验证无需创建WinUI的宿主入口；没有启动普通主窗口，不能替代正常GUI启动验收。广泛回归继续排除用户已停止的FailedMountTests、OrphanedMountRecoveryTests、ClearRemovalTests，不宣称0.0.9.33专项通过。

最终签名产物：

- x64：artifacts/packages/x64/HybridMount_0.0.9.37_x64_Test/HybridMount_0.0.9.37_x64.msix；SHA256 `EEBED0DA67AFCD8D290C1A5F7496A795AF52DB9B42F640864B94E61E523BF103`。
- ARM64：artifacts/packages/ARM64/HybridMount_0.0.9.37_arm64_Test/HybridMount_0.0.9.37_arm64.msix；SHA256 `C95886B22113F0BD6BA46CC27F7AA8EA7F7E5878097D5B7FC24088DC5935A5D5`。
- 以上是门控正式构建，没有安装或替换正式产品包。先前候选不作为可启用缩略图的正式交付。

## 独立清理审计

- 先前失败／超时留下的六个精确测试根已通过独立隔离管理器离线清空并注销；00937-failed-root-cleanup.json逐项记录Completed和NativeGone=true，没有连接任何源。
- 最终独立非打包进程同时读取64／32位真实注册视图，发现已注销测试根遗留16个32位CLSID。按精确路径、根注销证据、无共享引用及完整快照备份清理；00937-exact-clsid-backup.json和00937-exact-clsid-cleanup-result.json保留证据，文件删除0。
- b0f2f154／92d1fe3f的AddAsync注册失败前未发布RootId，夹具没有追踪到两项半成品。包外核验确认目录为空、CFAPI明确返回非同步根0x80070186，并按AUMID、当前SID、Namespace和目标路径配对备份／清理两个SyncRootManager叶键及两个32位CLSID。证据为00937-partial-register-native-state.json、00937-partial-registration-backup.json、00937-partial-register-cleanup.log。
- 最后00937-final-root-audit.json核验17个精确根和18个已备份CLSID：该清单的同步根／NameSpace／CLSID／隐藏值残留及临时包均0；既存正式注册保持。Explorer仍为原进程20428，没有主动重启。
- 本次扩大至32位只读视图还发现27304个未归属上述精确清单的测试CLSID引用，保持原状，不按前缀批量删除；没有充分证据区分各历史轮次，不能宣称全机注册无残留。通用移除的32位收尾不在本版修改范围，0.0.9.33专项仍停止。
- 共享库只读核验为schema1、bindings=0、images=0；保留一条指向正式目录的禁用授权记录，没有按测试前缀清除用户记录。00937-final-evidence.json保存统计，测试未删除正式内容。


## 未完成与待测试

- **已执行失败／功能未完成**：Explorer自动选择缓存缩略图，同时保全原生状态／系统菜单。需要受支持绑定契约或经验证可用的官方修正，不能以扩大文件关联、虚构CLSID、修改未公开根注册值或重启Explorer掩盖。
- **人工待测**：最终正式MSIX安装升级、Explorer默认状态列／图标／系统菜单目视与操作、现有正式挂载、实际大量媒体及DPI、ARM64实机。用户后续无法响应，按要求保留待测，不记为通过。
- 自有菜单继续暂停；不删除用户正式内容、不停止正式程序、不重启Explorer、不清全局缩略图或布局缓存。0.0.9.33专项不补跑。未归属本轮精确清单的32位注册清理另列后续工作，不能由本版审计推定完成。

## 参考

- Microsoft [CloudFiles声明](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-desktop3-cloudfiles)、[Cloud Files原生状态与系统菜单](https://learn.microsoft.com/en-us/windows/win32/cfapi/build-a-cloud-file-sync-engine)、[IThumbnailProvider](https://learn.microsoft.com/en-us/windows/win32/api/thumbcache/nn-thumbcache-ithumbnailprovider)。
- SDK26100的DesktopManifestSchema_v3.xsd与CloudFilesManifestSchema.xsd要求五子节点组完整或为空，各无CLSID节点虽合法却未定义默认委托语义；官方总览称optional，不能据此跳过实际SDK／部署／Shell门槛。文档参考不算测试记录。

GPT 6.1 Sol最后核对官方CloudMirror及CFAPI契约：根ID前缀不要求等于包名，ProviderId为可选且用于遥测，样例同样未设置；样例使用的IInitializeWithItem／IThumbnailProvider及STA已实现。未找到额外绑定接口的可靠证据，因此未改变正式根ID或猜测ProviderId。官方参考不算软件测试通过。
