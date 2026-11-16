# 0.0.25.2 验证记录

基线275f099。原创代码和文档添加标准MIT（dwzcn 2026），保留现有第三方许可、原文和准确出处；不推送、不上架、不安装包／信任、不操作正式挂载或重启Explorer。

## 已执行

- 最小x64 Debug非打包构建通过：artifacts/validation/00252-work/minimum.log。
- 统一检查核对61份许可文稿及清单，源原文正向对照通过；缺失根LICENSE、改变根LICENSE内容均被拒绝。证据license-payload-negative.json。实际MSIX及目录／ZIP结果按后续执行追加，不能把源文稿正向对照当作产物验证。
- 实际NuGet还原和nupkg、上游固定来源证据分别在resolved-audit.json、prepared-notice-changes.json及upstream/。未声明来自nupkg的AWS固定上游文稿，已在Provenance中区分。下载地址404／错误标签的尝试保留，不把失败下载当原文。
- SQLite DLL实际报告3.53.3；双架构与各自nupkg条目字节一致并包含同一source ID，见sqlite-native-evidence.json。这里没有加载ARM64 DLL或宣称ARM64实机通过。包装版本2.1.12与SQLite核心版本分别记录。

- Store策略夹具首次调用漏传WorkDirectory，没有执行成功；补齐隔离工作目录后27项通过，见store-policy-corrected.log，原错误输出保留。
- 检查发现已提交原文受Git自动换行转换，仓库.gitattributes改为保留第三方原文字节；不修改全局Git设置，也不清理上游空白。重新暂存后按字节核对。

## 回归范围

本版仅改原创许可、依赖说明、原文载荷和构建核验，没有改业务、安全判断、IPC2、schema9、本机定义标记及.hmxbk。完整功能基线是25.1的2239/2239与Acceptance4/4；本版不以旧通过数冒充当前回归结果。当前Debug／Release定向和ARM64交叉编译及发行结果另行追加。

## 待测与外部条件

WinUI／Explorer、DPI、高对比、21H2、ARM64设备、WACK、真正Store重签和干净系统安装／升级／卸载仍待测。自定义包检查不能替代认证。公开HTTPS隐私网址、真实支持方式、审核专用服务／账号及截图等未齐；0.0.25.2仍为准备版，不生成1.0、不上传。.NET／WinUI二进制继续受实际原包条款及第三方例外约束，根MIT不覆盖它们。

## 最终结果（2026-11-17）

| 验证 | 实际结果／证据（相对于artifacts/validation/00252-work/） |
| --- | --- |
| Debug定向 | **77/77**；20261116-171734-620-ca45ec/contracts-x64-Debug-Full.trx |
| Release定向 | **77/77**；20261116-171800-432-66af37/contracts-x64-Release-Full.trx |
| Store策略 | **27项**；store-policy-corrected.log |
| 双架构Full／Small Sideload | 4个MSIX、4份目录和4个ZIP均通过61份许可文稿／清单逐字节核验与现有架构／资源检查；delivery-<arch>-<mode>.log。Full目录x64为665文件、ARM64为661文件；Small各196文件 |
| Store双架构Full及容器 | 61份许可核验、包／bundle／msixupload检查通过；store.log。候选位于artifacts/store/0.0.25.2/preparation/20261116-172957-8d7644/，未上传，正式材料仍缺11项 |
| ARM64测试工程 | 交叉编译0警告0错误；arm64-tests-build.log；未执行ARM64设备测试 |
| 四个Sideload隔离副本 | SHA256开发测试签名，CMS核验有效；系统信任验证均返回1（未信任开发根）。无时间戳、未安装信任或应用，不等于发布签名／安装通过；signed-copies/results.json及逐包日志 |
| 原文字节 | 58份第三方文稿的Git过滤后与原始字节哈希一致；git-raw-license-check.json。重新暂存后62份文件（61份交付文稿加Stock原许可）的Git索引与工作区字节完全一致，见staged-license-bytes.json |

本版没有重新运行完整功能门禁。原生ARM64工具链仍可报告可选ATL/MFC LIB路径缺失及既有MUI覆盖提示，未将整体发行写成零警告。当前实现、可执行定向与发行检查均已完成；待测和外部材料仍按上文保留。
