# 0.0.25.1 验证记录

基线d060658。按已授权范围独立实现与本地提交，不推送、不安装生产包、不操作正式挂载或重启Explorer。.hmxbk schema1编码与凭据保护保持。

## 实施及阶段结果

- 最小x64 Debug首轮构建通过；后续新增schema完整性检查及接口清理后还须复验。测试工程首次编译通过，0警告0错误。
- 首轮定向Debug 170/192。新用例发现多语句ExecuteNonQuery未枚举全部schema核验查询，已改为完整读取NextResult；纯数据库旧夹具默认读取用户定义，已改为显式内存存储；旧IPC占位响应预期改为未实现。失败TRX保留于20261116-160521-004-ea5dbe/。未修改用户注册表或旧数据来使测试通过。
- 后续定向、完整门禁、发行与架构结果按实际执行追加，不预填通过。

## 旧测试替代清单

| 删除／变更的专用覆盖 | 当前替代行为 |
| --- | --- |
| StoreIdentityTests的旧包安装清单、拒绝启动及扫描失败用例 | 不再提供该功能；保留产品／模式范围、重置路径和构建身份维度行为测试，无安装包扫描 |
| OperationDetailsTests的schema7升级和升级失败备份2项 | FileTaskPipelineTests增加7／8拒绝及原字节保持；MountDataBaselineTests覆盖新9、缺表、不重建、配置预检与缺失索引；原展示／批次回归保持 |
| LegacyRecoveryRangeTests的旧源读取6例、ProtocolRecoveryTests旧模拟源迁出1例 | FileSourceBaselineTests拒绝null／数字0并核对旧源文件字节未变；现行HTTP取消／重定向和持久保护合并用例保持 |
| Enabled旧规则接受6例和缺字段推断 | 改为配置／备份边界拒绝用例；明密文.hmxbk、规则字面值及不可变性回归保持 |
| 共享缩略图库迁移、逐根迁移删除专用2项 | 旧共享库不读、不迁移、不删除；当前GUID缓存隔离、陈旧快照及共享内容拒绝删除保护 |
| 旧连续上传开关迁移1项 | 当前Queued重启恢复、Canceled代次不重放及本地正文保持 |
| LegacyChecked日志补写 | 当前schema3取消／CAS／不可变视图保护，schema2原字节保持并拒绝 |
| RestartRepairsLegacyClearedBlankPropertyWithoutReplayOrIdentityChange | 删除已移除的旧空白兼容修复专用用例；保留ActualPublisherPreservesForeignStateAndRemovesOwnIdentifier、当前取消／问题恢复和文件身份保护用例 |

不能将按授权删除旧功能的专用测试写成修复了25.0中的对应旧兼容失败。OutcomeUnknown、已发送请求核对、取消不回滚和当前注册恢复继续保留。

## 完整门禁首轮及修正

完整Release首轮2197/2239，11.7231分钟，原TRX为20261116-162617-712-4fc18d/contracts-x64-Release-Full.trx。32项来自注入存储预置定义但没有索引的旧夹具，已按新建索引→保存定义的顺序重建；4项批量移除夹具补明确协议；定义读取失败改为断言建库前拒绝。没有修改生产MissingIndex保护来迎合夹具。

另外，LocalReplacementTests的旧误分类授权补写3例改为未解决意图不从文件索引补造授权并保留正文／不写远端；ExcludedRenameFlowTests旧改名推断用例改为重启没有已捕获事件时不产生远端MOVE／DELETE。MountingTests删除跨测试旧注册自动扫描帮助器，只清理本用例确切拥有的现场，保留失败现场的保护不变。

相关Release定向首轮70/71（20261116-164458-917-e621b7/）。剩余改名用例因旧日志不再被创建，直接删除不存在的父目录路径导致错误，测试finally的已停止管理器又遮盖了原始异常；修正为存在时删除并先恢复可清理管理器后，20261116-164844-798-8e0bee/单项1/1。完整Release随后重新执行，最终结果另行追加。

这几项替代补充至上表：不再把旧索引／历史作为新任务的修改或删除授权；现行同名替换、冻结副本、OutcomeUnknown、显式重试、原子导出和批量移除流程的其他行为覆盖全部保留。

## 待测

真实WinUI／Explorer、DPI、高对比、21H2、ARM64实机、WACK、真正Store重签和干净安装／升级／卸载仍待测。跨架构编译不等于设备通过，自定义包检查不等于认证。未安装证书信任或包。

## 最终结果（2026-11-17）

- 完整Release功能 **2239/2239**：`20261116-164902-297-9773bc/contracts-x64-Release-Full.trx`。自动Acceptance首轮3/4的受管目录夹具同样先于索引预置定义，调整为新建索引后保存当前定义，业务保护未放松；独立复验 **4/4**：`20261116-170827-944-f36fa0/contracts-x64-Release-Full.trx`。
- 扩大后的最终Debug定向 **270/270**：`20261116-170856-387-f77727/contracts-x64-Debug-Full.trx`。覆盖新索引／配置、当前备份、取消恢复、复制快照、导出原子提交、移除和属性保护，保留前轮失败证据。
- 最终最小x64非打包构建、双架构Full Sideload MSIX检查通过，日志`final-minimum-after-fixtures.log`、`final-package-x64.log`、`final-package-ARM64.log`。两架构Full目录／ZIP此前同版本检查通过，分别656／652文件；本版未生成Small／Store新容器，不以25.0结果冒充本版实测。
- ARM64测试工程最终交叉编译通过，见`arm64-tests-final.log`；没有在ARM64设备运行。原生工具链可选ATL/MFC路径提示和MUI覆盖提示保留，不宣称全部零警告。
- 两个Full包隔离副本使用明确开发证书签名，CMS核验有效、签名者匹配；系统信任验证均返回1（未信任根），没有时间戳、安装信任或安装应用。证据`signed-copies/results.json`及逐包日志。此项不等于安装或发行签名通过。

最终100个暖态样本及固定7次查询实测：

- `rows=10000;samples=100;queries=7;P95=120.781ms;P99=176.219ms`
- `rows=1000;samples=100;queries=7;P95=9.460ms;P99=10.067ms`

只读旧库拒绝、配置标记12、schema9缺表拒绝及索引丢失保护均保持；拒绝时没有升级／备份迁移或重建原库。.hmxbk schema1和明密文编码未改变。25.0旧Cleared专用失败按授权移除其旧功能及用例，本版保留当前清除／取消保护并通过，不能写成修好了旧兼容代码。
