# 0.0.22.0 测试整理与替代覆盖

保留单一测试工程，按实际依赖区分纯逻辑与本地HTTP／SQLite／文件／原生集成。没有按比例删除测试。真实服务和长时脚本仍须显式执行，本轮未访问正式挂载或远端服务。

| 旧结构断言 | 处理与替代 |
|---|---|
| AppearanceSettingsTests.Production_entry_is_unified_and_default_store_is_internal_in_same_file | 删除“同文件、默认构造、具体存储不可公开”的实现文字断言；保留隔离注册表往返、读写失败不覆盖和通知行为。新门禁禁止设置服务绕回WinUI，显式注入存储由实际编译核对。 |
| MountingResponsibilityTests.Shared_models_only_use_localized_display_without_runtime_or_native_dependencies_and_service_roots_are_classified | 删除旧根目录／方法前缀及特殊资源例外检查；统一门禁覆盖全部Models／Commons禁止依赖、partial和命名空间，实际规则、仓库存储失败行为保留。 |
| ProtocolModuleBoundaryTests.Protocol_implementations_and_wire_dependencies_stay_inside_modules | 删除以Models/协议旧目录判断SDK泄漏的检查；统一门禁保护层次，工厂初始化失败释放、只读测试不挂接运行时、恢复与协议身份行为保留。 |
| PageViewModelTests.Every_page_owns_exactly_its_named_viewmodel_and_viewmodels_have_no_view_dependency | 删除逐页内部字段／类名文本检查；保留全部草稿、取消、设置保存、选择和退订回归。实际生产XAML编译、headless生产链接和统一类型／路径门禁校验引用。 |

新增SourceStructureTests验证禁止引用、注释／消息不被误判和partial分散；DiagnosticBoundaryTests验证四种严重程度的真实日志持久化与延迟Dispatcher发布。其他导航动画／历史、清单、COM和原生契约检查保留，只更新实际路径。

共享夹具不复制实现，每个用例仍创建独立实例：

| 原所属测试 | 当前夹具 |
|---|---|
| HttpTestServer | Fixtures/Protocols/HttpTestServer.cs |
| WebDavTests.DavFixture | Fixtures/Protocols/DavFixture.cs |
| RemoteCollectionSyncTests.SyncDavFixture | Fixtures/Protocols/SyncDavFixture.cs |
| S3Tests.S3Fixture | Fixtures/Protocols/S3Fixture.cs |
| UploadProtocolTests.DavLockFixture | Fixtures/Protocols/DavLockFixture.cs |
| WebDavNetworkTests.TlsFixture | Fixtures/Protocols/TlsFixture.cs |
| SynologyTests.SynologyFakeDsm | Fixtures/Protocols/SynologyFakeDsm.cs |
| TestSourceFactory | Fixtures/Protocols/TestSourceFactory.cs |

23个测试文件按实际HTTP／存储或生命周期依赖重新分类；Architecture只保留共用门禁验证。ProductionSources.props以功能目录登记生产链接和明确headless排除，构建核对实际Compile集合。没有生成第二个测试工程或复制生产实现。

迁移前完整Release基线为2151通过／2158总数、7失败。两项旧结构／异常类型断言及额外权限查询计数按当前行为修正；WebDAV共有70ms期限测试改用可取消HTTP适配器，保留1—3次请求和同一总期限，真实重定向服务覆盖不减少。TaskSnapshotCostTests的100样本及P95阈值未改，移到Acceptance/Performance，完整门禁在功能测试后单独顺序执行。P1上传类型变化和群晖取消的基线失败先保留，定向复验通过；最终完整结果另见验收记录，不把基线失败冒称本版引入或自动忽略。

```powershell
# 完整门禁：功能集合后顺序执行Acceptance
.\build\Build.ps1 -Platform x64 -Configuration Release -Test

# 明确分组；均使用相同隔离原生映像
.\build\Build.ps1 -Platform x64 -Configuration Debug -Test -TestGroup Unit
.\build\Build.ps1 -Platform x64 -Configuration Debug -Test -TestGroup Integration
.\build\Build.ps1 -Platform x64 -Configuration Debug -Test -TestGroup Native
.\build\Build.ps1 -Platform x64 -Configuration Debug -Test -TestGroup Architecture
.\build\Build.ps1 -Platform x64 -Configuration Release -Test -TestGroup Acceptance
```

默认完整门禁继续排除先前已停止的FailedMountTests、OrphanedMountRecoveryTests、ClearRemovalTests三个专项，未新增排除。性能从混跑移为独立顺序执行，没有从完整门禁移除；环境及实机限制见验收文档。分类、移动前后规模和运行结果保存在artifacts/validation/00220-work/。
