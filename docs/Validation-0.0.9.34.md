# HybridMount 0.0.9.34 验收记录

日期：2026-11-08；执行环境：Windows x64，.NET SDK 10.0.401、VS 2026 v145、Windows SDK 10.0.26100.0。开发及自动验证预算4–8小时，桌面等待另计。

**代码完成，相关回归与构建通过；未宣称全量回归或全部Explorer显示已通过。** GUI与ARM64实机项目如下保留。0.0.9.33已停止的专项验证未继续补跑，本记录只描述0.0.9.34实际执行结果。

## 范围及代码

- 新建根共用`CloudRootRegistration.CreateDescriptorAsync`，明确`AllowPinning=true`；保留Partial水合、AlwaysFull填充、Context／文件身份、固定及同步保护。已有根没有新增迁移、重注册或列布局修复流程。
- 真实进程包身份决定运行和配置模式；诊断同时显示包身份、配置模式、根AUMID及暂停原因。输出路径不参与判断；两模式配置保持独立。
- 正式清单只含提供程序图标的空CloudFiles声明，保留App、启动任务和隐藏win32App／mediumIL维护入口。没有自有COM类、fileExplorerContextMenus或空处理器注册。保留三个工程和原生实现。
- `ShellExtensionPolicy`默认暂停；生产管理器在路径／缓存／正文查询前阻止预览获取、排队和渲染。旧IPC v2请求返回`unsupported`／`disabled`，不启动后台读取；隐藏所有挂载卡片和协议配置缩略图开关，不把已有偏好保存为false。缓存和备份字段保持。
- 保留预览实现的回归通过每个测试实例的`HYBRIDMOUNT_HEADLESS_TEST`开关显式启用；生产编译没有该开关。没有恢复自有处理器注册。
- 系统激活的维护子进程提前保留`SafeHandle`，使退出后仍能读取退出码。此问题在本版临时MSIX收尾时发现；最终真实包维护流程完成，未将其等同于0.0.9.33所有移除场景已验收。
- 公共文件源接口、SQLite、配置、备份及IPC版本未改变。所有目标文本通过`patch/0.0.9.34_*.patch`检查、应用后删除；无关暂存及用户签名配置保留。

## 隔离真实Shell对照

使用本轮GUID命名的独立NTFS根、隔离索引／测试注册配置及本地回环DAV服务，不访问正式WebDAV／S3，不操作现有MySvr等挂载。生成自包含宿主直接复用生产挂载、注册和维护代码；不增加第四个工程、不创建WinUI／托盘或公共IPC。打包场景由`IApplicationActivationManager`激活，已核实实际包身份。它证明底层注册和Shell API行为，不代替主程序完整安装与GUI验收。

| 配置 | 实际结果 |
| --- | --- |
| Unpackaged | 无实际包身份；新根注册成功；最终API断言通过 |
| IconOnly临时MSIX | 有实际包身份，根AUMID指向该包App；空CloudFiles注册成功；最终API断言及真实维护收尾通过 |
| None临时MSIX | MSIX构造、签名、安装和激活成功；WinRT注册新根返回`0x80070490`；未进入显示验收，原生根及路径注册经包外核验不存在 |

因此保留默认的空CloudFiles声明；不采用无声明方案，不添加虚构或未实现处理器。

最终Unpackaged和IconOnly均从物理路径及`::{NamespaceCLSID}`命名入口查询：

- `IShellFolder2`绑定成功，均暴露`System.StorageProviderUIStatus`列；文件菜单含Windows原生“始终保留在此设备上”和“释放空间”，无本产品菜单。
- `ICONONLY`取得图标，两个入口的txt类型位图哈希一致；这里只验证API和txt样本，PNG等实际图标外观仍待目视。
- 原生及Shell状态：仅占位为仅联机／未完整；缓存为已同步／完整；固定为PINNED／完整；释放后为UNPINNED／未完整。三个未提交文件仍为等待上传，未强行改InSync。
- 真实目标`/dav/uploading.txt`的PUT响应被隔离服务挂住，持久记录为`Sending`；另一个目标403后为`Failed`，还有本地编辑尚未提交。避免把前置能力探测PUT或仅有根IDLE当作上传中／已恢复证据。
- 检查前后CFAPI身份、PinState、完整缓存及Shell状态快照一致；每个场景源请求均`24 → 24`，旧预览请求为disabled。
- 结束后按产品清空／注销收尾流程清理测试根，由独立非打包进程核实`GetCurrentPackageFullName=15700`、原生查询`0x80070186`、真实HKCU／HKLM中无精确根、Namespace／CLSID、隐藏值及目标路径引用；临时包已全部卸载、空测试根目录已删除。没有重启Explorer或清除全局布局。

证据：

- `artifacts/validation/00934-native-5012b47a/Unpackaged-TargetPut/result.json`
- `artifacts/validation/00934-native-5012b47a/IconOnly-TargetPut/result.json`
- 上述`result.json.outside.json`及打包场景的`package-removed.json`
- `artifacts/validation/00934-native-5012b47a/None-Final/result.json`、`verification-target.json.outside.json`
- `artifacts/validation/00934-native-matrix-summary.json`（两模式各17项API断言）
- `artifacts/validation/00934-native-cleanup-final.json`

`GetDetailsOf`的复合状态文字为空，`GetDefaultColumnState`没有证明Explorer默认可见列。没有把这些API数据当作“状态列默认显示”或图标绘制验收通过。

## 自动验证与构建

| 验证 | 结果／证据 |
| --- | --- |
| 最小x64 Debug非打包编译 | 通过；`00934-batch1-build.log` |
| 首轮针对性 | 45通过／1失败；`00934-focused.trx`。错误假设当前身份一定能枚举新WinRT根；调整后46/46，`00934-focused-final.trx` |
| x64 Debug构建与全套首轮 | 构建通过；1182项中1178通过／4失败，0跳过；`00934-debug-regression.log`、`contracts-x64-Debug.trx` |
| 修正后相关Debug回归 | 132/132，0跳过；`00934-debug-relevant.trx`。涵盖生产暂停、保留预览实现、页面、原生固定／释放、Shell状态、上传和退出 |
| x64 Release非打包构建 | 通过；`00934-release-unpackaged.log` |
| 相关Release回归 | 172/172，0跳过；`00934-release-relevant.trx`，包含上列功能及配置／容量备份兼容 |
| ARM64测试代码 | 交叉编译通过，未运行；`00934-tests-arm64-build.log` |
| x64／ARM64 Release MSIX | 构建、SHA256签名验证、架构／资源／自包含／清单检查通过；`00934-package-x64.log`、`00934-package-arm64.log` |
| 实验入口防回流 | Build.ps1参数及直接MSBuild启用均拒绝；`00934-experiment-guard.json` |
| 编译登记 | 主工程Compile204、Page10；测试Compile279；均无重复且包含策略文件，`00934-compile-items-summary.json` |

首轮全套失败及处理：

1. `PreviewTests.ColdAndWarmCloudExePreviewsNeverHydrateAndLocalExtractionSurvivesDehydration`、`ShutdownTests.BlockingS3IoIsCanceledBeforeClientRelease(preview)`调用已暂停能力而未显式启用保留实现，已修正测试实例并在相关Debug／Release中通过。生产默认暂停保持。
2. `FailedMountTests.UnknownRegistrationWithMaterializedIndexRequiresRecovery`期望Failed而得到Completed；`FailedMountTests.ForeignRootRejectedAndNativeUnregisterOutcomeHandled`最终清空期望Completed而得到Failed。这两项在本轮未通过；本版未修改其断言或移除阶段规则，未重新运行0.0.9.33以定责，不把全套记为通过。后者留下的本轮精确模拟测试根已离线清理，并从独立非打包进程确认无注册残留；证据`00934-regression-cleanup.json`及`.outside.json`。

技术验证期间还保留了以下失败证据：初版探测器把普通本地修改文件当作云文件、Shell绑定GUID错误、初版仅阻塞到能力探测PUT（Prepared）、临时包身份长度超限。这些均修正后取得最终对照，不能用早期结果冒充完整七状态验收。首个临时包的维护退出码失败也保留原始报告，离线收尾后包外确认，最终包已通过产品维护流程。

产物：

- `artifacts/packages/x64/HybridMount_0.0.9.34_x64_Test/HybridMount_0.0.9.34_x64.msix`
- `artifacts/packages/ARM64/HybridMount_0.0.9.34_arm64_Test/HybridMount_0.0.9.34_arm64.msix`
- 哈希：`artifacts/validation/package-x64.sha256.txt`、`package-ARM64.sha256.txt`。未安装这些正式产品包。

## 待办与限制

- [ ] 正式主程序MSIX安装／激活，以及非打包主窗口启动和页面目视。
- [ ] 两种模式分别通过新命名入口及磁盘路径打开；确认默认详细信息状态列、不同文件类型图标和原生固定／释放菜单外观及交互。
- [ ] 云端、本地、固定、释放、本地修改、上传中及失败的真实Explorer绘制与刷新时序。
- [ ] UI缩略图开关隐藏、溢出分隔符和固定第三行高度的主界面目视。
- [ ] ARM64实机安装、运行与Explorer验收。
- [ ] 既有两项移除回归未通过；按用户停止0.0.9.33专项验证的边界保留，不纳入本版扩展暂停验收通过项。

电脑交互工具在恢复、重置后仍以trusted Node退出／`windows sandbox failed: helper_unknown_error: setup refresh had errors`失败；按工具恢复上限停止，没有改用其他UI输入方式。未执行的GUI项没有记为通过。

本版不处理旧根迁移、注册修复、列布局或缩略图恢复，不改变水合策略，不接管另一运行模式的根。完成后独立本地提交，不推送。
