# 0.0.9.64 大目录发现与分批发布验收

## 代码与边界

基于2ad309a。每轮复用本地entries、远端shadow及collections索引，路径、条目ID和父子关系字典避免逐项重新读全根；执行前仍以点查重新核验当前映射／待处理事件，不用旧快照授权覆盖。新增entries_source、entries_local及remote_pending_path兼容索引，SQLite user_version仍为6，没有新表／字段。

完整列表每256项持久正向发现并创建冷占位；RFC初始续页与S3无分隔符分页同样分批。父先子后，原生CfCreatePlaceholders逐父目录最多256项，失败逐项回执且不覆盖已存在文件。先提交准备ID、事务外CFAPI、再提交实际结果；长路径按768KiB参数预算拆分，公共IPC v2保持。只有全部枚举及逐项缺失确认成功才提交删除，部分检查点保留不完整状态，重连从根去重重扫；250ms合并发现／创建／发布进度。

群晖列表仅取元数据，不逐文件启动MD5；未知摘要不按可靠版本跳过。干净零本地范围的冷占位首次读取取得真实摘要后绑定，迟到并发读取只能共享同一强版本。已有本地范围、修改、未决事件或身份改变仍走原保护路径。固定下载允许同条目同大小／时间线索的raw→已绑定MD5升级，两个强版本不等继续拒绝。

旧单项创建及兼容快照接口保留，C#仍为持久索引写入者，不在数据库事务内等CFAPI或远端IO。四工程、HybridMount.exe、NativePreview身份、配置／备份／公共IPC兼容。不把旧PopulateAsync兼容入口描述为本次优化主路径；常规应用保护中仍有逐项上传查询。

## 实际验证

证据目录：artifacts/validation/00964-directory/。

| 项目 | 结果 | 证据 |
| --- | --- | --- |
| x64 Debug／Release最终相关矩阵 | 各192/192，0跳过 | delivery-Debug.trx、delivery-Release.trx |
| 恢复夹具补强后双配置 | 各1/1 | receipt-boundary-final-*.trx |
| 流式及分页补充 | 47/47 | publication-edges-Debug.trx |
| x64双配置非打包 | 通过 | final-build-x64-Debug.log、delivery-build-x64-Release.log |
| x64／ARM64 Release签名MSIX／包检查 | 通过 | delivery-package-*.log |
| ARM64测试工程 | 仅交叉编译通过 | tests-ARM64-Release-compile.log |

在隔离NTFS／真实CFAPI根中，向远端夹具加入1000文件，第300项前暂停发现，前256项已持久并可见、连接仍未完成且没有GET正文；继续后逐条核对所有远端文件／目录和挂载身份。取消保留已发布批次，重连保持原ID。验证深层父子目录，重建“占位创建成功、物化事务未提交”数据库边界后保持原文件身份并恢复发布。普通本地文件在两批之间出现时正文不变、标记冲突，其他项继续。

S3真实AWS SDK对隔离HTTP夹具逐页读取，超过256项后注入503，首批保留且不发布删除；完整重试后才确认缺失。RFC部分初始页保留正向发现、不保留增长的Baseline数组，重开从根重新枚举且不误删。包含群晖读取／共享缓存、固定／释放、远端更新／删除、WebDAV和S3既有保护回归；不是实际DSM或远端提供商验收。

## 阶段失败与修正

- 初始21项失败全部因隔离原生映像仍为.63；更新.64后21/21。
- 流式集成的一次nullable编译错误已修正；早期大目录夹具缺少真实Move／Delete签名，补齐后编译。
- 群晖联合79项首次78/79，raw冷版本被首读合法绑定后固定调用持有旧版本，导致保护误拒；修正同条目升级衔接后固定专项16/16，最终矩阵覆盖通过。
- 首轮大目录4项中两项硬编码1001，遗漏默认目录／根系统文件；改为按远端条目逐项身份核验，原TRX保留，不把夹具计数误当产品故障。
- RFC旧夹具要求部分枚举不保存任何正向发现，与本版分批契约不符；改验“已发现可用、不得删除、重开重扫”。
- 审查发现恢复夹具使用SaveEntry无法回退materialized（产品保护故意取MAX），补为明确设置测试持久边界并断言标志确实为false；首次补强测试的清理因测试Sqlite默认池持有mounts.db失败，关闭该夹具连接池后双配置各1/1。没有改动产品池策略。

## 交付与待测

D:/src/HybridMount.NET/artifacts/packages/ARM64/HybridMount_0.0.9.64_arm64_Test/HybridMount_0.0.9.64_arm64.msix

SHA256：04CEEFAC8FB7DF55AE8331F852A22D67DAB2E5BE7B9F91BCB947FF38F45E26EA

D:/src/HybridMount.NET/artifacts/packages/x64/HybridMount_0.0.9.64_x64_Test/HybridMount_0.0.9.64_x64.msix

SHA256：7D4ADBAF822D42EFB0136F50B98B4DB871910A524D0D722C1DACD990F73238DC

真实DSM／S3大目录与弱网、人工WinUI／Explorer交互、实际创建期间任意时刻进程崩溃、正式安装升级和ARM64实机待测。未操作正式挂载、未重启Explorer、未推送或修改master，未补跑.33停止专项；.65—.68继续按已授权计划实施。
