# 0.0.25.0 验证记录

基线4b23072，实施用户批准的Store准备计划；当前记录按实际阶段更新，未执行的项目不记为通过。不推送、不上架、不安装生产包、不操作正式挂载、不重启Explorer。

## 已执行

| 范围 | 结果及证据 |
| --- | --- |
| 首轮最小x64 | 第一次因新增MSBuild条件引号失败；改为中间版本属性后通过。初期离线政策文件名产生PRI警告，改为无资格符的固定文件名后Store双架构包未再出现该警告。 |
| 身份／构建／结构定向Debug | 33/33；artifacts/validation/00250-work/20261116-143137-938-116a6c/contracts-x64-Debug-None.trx。前一轮32/33为新断言误把Windows用户名当产品名，已修正，原TRX保留。 |
| Store策略夹具 | 27项，含非法正式版本、缺少材料、错误渠道／运行时／签名以及公开URL条件。准备版preflight明确ReadyToSubmit=false，未生成1.0或上传。 |
| 完整Release功能 | 2236/2238，耗时14.7933分钟；20261116-143728-245-3726fe/contracts-x64-Release-Full.trx。不能写为全通过。 |
| 完整门禁失败复验 | LocalThumbnailCacheIntegrationTests.CachedBlobSurvivesDehydrationAndMainManagerExitWithoutAnyNewRemoteRead在整组中返回Waiting，隔离复验通过；ShellCustomStateRepairTests.RestartRepairsLegacyClearedBlankPropertyWithoutReplayOrIdentityChange期望Canceled但得到Queued，隔离仍失败。release-failures-recheck/recheck.trx保存1/2结果。 |
| 首个双架构Store候选 | 两包自身均通过。容器重检首次因bundle小写arm64传给资源门禁失败，修正规范拼写后实际msixupload／bundle／两个Full包检查通过；最终补齐运行时许可后重建结果见后续记录。 |

旧Cleared兼容路径和该回归用例在本版未作业务修改；没有将这一事实写成已完成基线复验或已修复。25.1按已授权兼容清理删除该路径，逐项记录由当前状态清除／取消／恢复用例替代。一次隔离复验通过也不代表普通目录／Explorer长时验收已完成。

## 身份与权限核对

- 包名RyleD.HybridMount.NET、Publisher CN=B14891C7-3483-4FD8-AC49-EA066227CC77，PublisherDisplayName RyleD；清单、构建属性、包查询、资源检查和夹具同步。
- 打包配置、实例和根范围不再共用旧产品；实际PFN由Windows返回，不手算。包全名、映像、SID、会话、版本及私有随机握手继续限制原生对端；维护入口新增包全名核对。
- Packaged的托管缓存和原生读取均定位实际包LocalState；非打包范围保持。实际包检查拒绝unvirtualizedResources及虚拟化排除，并保留runFullTrust。
- 正常注销仍使用CFAPI／StorageProvider官方入口；维护仅收尾有精确当前产品、用户、根和注册证据的残留，不授权任意注册路径、另一产品、目录递归或权限提升。
- Microsoft关于win32App仍具包身份及运行行为的说明用于核对设计：[运行模型](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes)。隐藏维护入口的真实新包激活仍须隔离安装验收，未以文档替代实测。

## 签名及外部材料

短期证书明确标注DEVELOPMENT TEST ONLY，匹配新Publisher，私钥不可导出，未安装信任；仅公钥与指纹记录于忽略目录。Store候选保持未签名。侧载测试签名的密码学验证与系统信任分别报告，不把未信任开发证书称为可直接安装的发布签名。

公开HTTPS隐私网址、真实支持方式、审核专用服务和账号、真实运行截图、年龄分级及最终发行资料未齐。docs/store为三语言发布前材料，不等于已托管或已上架。.hmxbk无密码时包含明文凭据且不包含文件正文。

## 待测及边界

WACK工具存在，但其实际候选测试、Store重签、干净系统安装／升级／直接卸载、旧包与新包真实多开、移除unvirtualizedResources后的实际包缓存和注册恢复、WinUI／Explorer、DPI、高对比、21H2及ARM64实机未完成。当前不安装生产包或以当前现场作卸载试验。ARM64交叉编译与自定义包检查均不能替代这些验收。

## 最终发行矩阵与收尾（2026-11-16）

- Sideload x64／ARM64 × Full／Small：4 个 MSIX、4 份目录和 4 个 ZIP 均通过当前包内容、资源和架构检查；Full 目录分别 656／652 文件，Small 两架构各 187 文件。最终记录位于本证据根的各 success-Package／Publish 和 delivery-* 目录。
- Store 最终双架构 Full 包及 `.msixupload`／bundle 重检通过：`artifacts/store/0.0.25.0/preparation/20261116-154255-0728fc/`。Store 候选无侧载签名，正式材料仍缺 11 项，未上传。一次重建被 7-Zip 占用阻止，用户关闭后重建成功，原失败记录保留。
- 最终 x64 Debug 非打包构建通过；定向 Debug 为 **97/98**，TRX：`20261116-154815-379-188b05/contracts-x64-Debug-Full.trx`。缩略图释放用例任务完成后仍观察到完整内容；`debug-thumbnail-recheck/` 隔离 **1/1**。与 Release 整组失败和隔离通过一并保留，尚未证明其基线归因或消除组间不稳定性。
- 独立 Release Acceptance **4/4**：`20261116-152607-058-4d6e0f/contracts-x64-Release-Full.trx`。ARM64 测试工程交叉编译 **0 警告、0 错误**，见 `arm64-tests-build.log`；未在 ARM64 设备运行。
- 四个 Sideload 应用包的副本完成 SHA256 测试签名，CMS `CheckSignature(true)` 均通过、签名者匹配明确标记的开发证书；`signtool verify /pa` 均返回 1，原因是开发根未受信任。CMS 核验不等于整个包已通过系统信任或安装测试，签名未加时间戳，结果见 `signed-copies/results.json` 与逐包日志。未安装证书信任或应用。
- 原生 ARM64 工具链报告可选 ATL/MFC LIB 路径不存在；隔离 Debug 原生资源重建报告既有 MUI 覆盖提示。不能将本轮全部构建写成零警告。

本版已完成实现、可在当前环境执行的构建及上述验证；完整功能门禁仍有明确记录的失败，未宣称全部回归通过。后续25.1按授权删除旧Cleared兼容路径及专用测试，同时保留当前清除、取消和恢复保护验证。
