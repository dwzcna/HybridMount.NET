# 0.0.20.0 验证记录

基线36c4b6f／0.0.19.0；2026-11-15。源码、程序集及MSIX统一0.0.20.0。各阶段证据位于 artifacts/validation/00200-work/，实现完成与实际验收分别记录。

实现包括独立 operation_runs／operation_steps 展示证据、按ID原位详情及64次尝试／256成员分页、remote_task_batches／remote_task_batch_members 与检查点同事务、冷占位合并和实际水合状态的独立下载、原生排他冷核验、取消分类和尝试隔离、历史清理保护、schema7→8一致性备份和事务升级。公共IPC及.hmxbk保持。

| 项目 | 实际阶段结果 | 证据 |
| --- | --- | --- |
| 最小产品及原生 | x64 Debug非打包通过 | debug-integration.log、debug-expanded.log |
| Debug | 首轮64/64；扩大130/130；历史及步骤修正18/18；后续45/45；最终组合104/104，追加原生Pin保护8/8 | debug-final.log／trx、pin-guard-final.log／trx及相应阶段记录 |
| Release组合首轮 | 308中305通过；两项为空状态SQL绑定和内部成员清理计数，已修正并定向复测。另1项万项快照P95受同机组合负载影响，未当作通过 | release-expanded.log、release-expanded-initial.trx |
| 性能隔离 | 最终Release每规模暖态100样本，查询数固定7；1000项P95=10.177ms／P99=11.861ms；10000项P95=134.675ms／P99=180.059ms，通过原150／750ms门限。此结果只表示隔离快照，不代表Explorer或生产负载 | snapshot-performance-final.trx |
| 实际WinUI | 当前产品控件三语言×明暗×600／1100宽，Debug／Release各12组通过；检查步骤对象原位刷新、失败详情、历史折叠状态。探针使用独立空配置和数据目录，不连接正式挂载 | ui-debug.json、ui-release-final.json |
| 交付 | 最终x64／ARM64 Release自包含签名MSIX、验签及包检查通过；ARM64测试交叉编译0警告、0错误，未执行ARM64测试 | release-final.log、package-arm64-final.log、tests-arm64-final.log |

最终Release组合179/179通过，涵盖最终普通FETCH实际收尾、新旧任务身份、Pin恢复及历史清理代码。Debug追加原生8项包含零字节UNPINNED和恢复前外部Pin改变。双架构产物均在最终原生修复后重建。

新增回归覆盖7→8备份与升级失败回滚、未执行阶段不伪造成功、尝试保留及分页、768项跨分块单一批次、独立下载与取消、三组固定冷／热示例、实际CFAPI范围与零字节状态、原生保护句柄拒绝热文件冷刷新、旧尝试回执拒绝、索引移除后的历史及归属保护。最终实际执行数量以对应TRX为准。

过程失败保留：原生UNPINNED更新在中断恢复时失败；数值检查点确认CldFlt保护打开会改变Pin。修复为先持只共享读取的属性句柄核验Pin／身份，再取得保护引用；恢复前用户已改Pin则拒绝，不放宽外部修改保护。错误诊断补齐固定阶段白名单。Preview第二次释放原用等待态当完成、ClearRemoval使用合并通知抢取消的测试竞态，分别改为真实终态和逐项检查点，断言没有放宽。

其他阶段失败保留：资源校验识别缺少明确映射／语义键，修正后继续；独立探针首次引用产品PRI导致App.xbf重复，改为单独程序集引用后编译并完成实际WinUI验证。未用失败、计划或历史版本结果宣称通过。

待测：完整产品交互、所有协议与真实服务器的批次规模、DPI／高对比度／辅助功能、Windows11 21H2、ARM64实机及生产长期压力。隔离控件探针和交叉编译不替代这些验收。不安装生产包、不操作正式挂载、不重启Explorer；本版独立本地提交，不推送。
