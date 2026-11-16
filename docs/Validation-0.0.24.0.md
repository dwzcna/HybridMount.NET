# 0.0.24.0｜构建复用与 Full／Small 交付验收

证据目录：`artifacts/validation/00240-work/`。本机 x64、VS 2026／固定 SDK 10.0.401；没有安装交付包、连接正式挂载或重启 Explorer。

## 已执行

| 检查 | 实际结果与证据 |
| --- | --- |
| 当前 Debug 定向 | 31/31，`final-debug-current.log`、`20261116-075122-148-3d48e9/contracts-x64-Debug-None.trx`；仅准备 None 夹具，没有构建主程序和 C++ |
| Release 完整功能门禁 | 2228/2228，`20261116-073516-471-74099a/contracts-x64-Release-Full.trx` |
| 独立验收 | 4/4，同运行目录 `acceptance-x64-Release.trx`；功能与性能验收顺序执行 |
| ARM64 测试交叉编译 | 0 警告、0 错误，`final-arm64-tests.log`；未运行 ARM64 测试 |
| 缓存与 IDE 登记 | 19 项行为检查，`cache-behavior-current.json`；涵盖坏／缺记录、属性、输出变动、夹具选择、真实 MSBuild 采集与成功／失败标记 |
| 托管架构边界 | 6/6，`managed-architecture-checks.json`；AnyCPU 在两架构可接受，要求32位、错误架构和原生映像不会误当可用托管投影 |
| 安全启动分支 | 7/7，`launch/results.json`；下文说明范围 |

`incremental-matrix/results.json` 保存新增、保留时间戳修改 C#、改名、删除、XAML 修改／恢复、RESW 修改／恢复及输出删除的九项执行记录。C# 常量由最终 PE 元数据读取，确认没有返回旧类型／内容。最初的全部文件字节比较发现语言卫星的不确定性，未把该轮判为完整通过。

修正后 `incremental-safety/results.json` 验证输出丢失重建、编译失败不留成功记录、失败后恢复；629 个实际输出的名称及字节摘要全部与官方完整基线一致。`semantic-incremental/results.json` 另将真实 XAML Tag 与 RESW 文案同时改动，确认它们进入最终 PRI；恢复原字节和原时间戳后，完整产品输出重新一致。临时源码均已恢复，备份和过程失败证据保留在忽略目录。

## 构建耗时

当前测量见 `current-performance.json`，同一PowerShell进程连续调用，外层墙钟包含脚本调用；每轮记录官方目标、执行／复用状态、还原、求值、输入及输出核验耗时。

- 0.0.23.5 的五次无变化脚本构建：23.4964、22.8211、22.0901、22.1672、23.6420 秒，中位 **22.8211 秒**。
- 本版最终五次无变化：**5.726、5.692、5.683、5.711、5.654 秒**，中位 **5.692 秒**，全部复用完整托管构建；相对此基线约减少 **75.1%**。
- 最终首次执行：**121.920 秒**。输入有变或输出缺失时采用官方 Rebuild，并核验完整输出；不能把无变化成绩推广到编辑后的构建。
- 冷轮输出摘要核验约32秒，后续相同文件约0.4秒；已单列成功记录与文件诊断。没有略去这项成本或降低输出保护。

同版早期完整重建 binlog（`cold-target-timings.json`）中，Csc 主编译约9.59秒、XAML预编译约8.42秒、两次 CompileXaml合计约11.27秒；资源与结构目标合计约1.45秒。这是完整重建证据，不是对用户 VS 无变化场景的耗时归因。 最终冷轮的官方托管链为76.99秒，其中XAML预编译39.49秒、两次XAML编译合计23.60秒、主Csc9.43秒；输入摘要6.97秒、构建后输入复核1.83秒、输出核验31.15秒。波动原因未确认，`current-cold-target-timings.json`保存当前观测，未用早期较快结果替代。

用户提供的 VS 日志显示无变化生成27.740秒、改动一个原生文件后20.233秒；本机 `devenv /Build` 两轮成功，含启动总耗时34.13／21.41秒（`vs-cli/`）。该入口仍进入 WinUI 链，**没有证明交互式 VS 快速生成已经跳过，也没有将脚本约5.7秒写成 VS 成绩**。

最终按真实设计时条件采集（`vs-design-time-registration.json`、`vs-registration-final-summary.json`），确认语言资源、项目／导入、原生源码及两个实际原生复制源均被登记，缺失输入为零。成功标记与输入同组，稳定载荷在独立集合中只核验存在，避免拿旧复制物时间戳与所有 C# 输入混比。采集目标及失败不写标记由真实 MSBuild 夹具验证。依据：[CPS 快速生成说明](https://github.com/dotnet/project-system/blob/main/docs/up-to-date-check.md)。隐藏 DTE 探针未能取得已加载主项目／输出窗口，错误单列保留；交互式 VS 现场复验仍待执行。

## 交付矩阵

四个发布配置分别隔离架构、配置、运行时、部署、Shell和构建用途；没有修改四工程身份、数据库schema8、公共IPC v2、hmxbk、COM或包身份。

| 架构／模式 | 签名 MSIX | 非打包目录＋ZIP | 目录文件数 |
| --- | --- | --- | --- |
| x64 Full | 通过 | 通过 | 608 |
| x64 Small | 通过 | 通过 | 139 |
| ARM64 Full | 通过 | 通过 | 604 |
| ARM64 Small | 通过 | 通过 | 139 |

日志为 `final-matrix-<架构>-<模式>.log`；实际三语言 PE／PRI／MUI、图片、许可证、主映像／SQLite／原生宿主／框架的架构、版本、身份、COM注册和生产宿主不含测试入口均已核对。WinUI托管投影为AnyCPU，按CLR标志验证，不误报为x86本机依赖。四个主MSIX均使用本机既有证书签名、验签，无签名时间戳；没有安装。构建工具生成的 `_Test/Dependencies/` 不在主MSIX载荷内。

Full 实际含.NET和Windows App Runtime框架载荷；Small不含完整框架，runtimeconfig声明.NET10，MSIX声明 `Microsoft.WindowsAppRuntime.2 >= 2.5.1.0`，目录含引导组件。关闭R2R、符号／文档发布、裁剪、AOT和单文件，保留三语言与实际依赖。原生工程继续使用既有静态CRT。

`unchanged-delivery-recheck.log` 验证命中成功记录时，MSIX、目录和ZIP的实际资源检查仍重新执行。最终IDE登记补齐后又执行 `final-ide-delivery-recheck.log` 的x64 Small完整打包／发布与资源复核；该改动只涉及IDE输入登记，不改变业务／原生实现。

安全启动仅使用已有 `--preview-worker` 早期返回分支，在App、托盘、挂载及网络服务创建前退出：Full／Small目录及ZIP提取版各成功返回2；用仅含hostfxr、无shared框架的子进程DOTNET_ROOT，Small报告缺少.NET10（0x80008096），Full仍返回2；ARM64映像在x64上被系统以216拒绝。环境仅影响子进程，没有修改本机安装或全局环境。这些证据不等于完整界面和文件同步运行通过。

## 过程修正与待测

- 早期探针取错目标框架导致路径失败，修正后重跑；中断产生的自有探针已精确清除，原XAML和资源恢复，无用户文件被回退。
- 两份卫星DLL在连续官方重建中都变化，`output-comparison/` 保存逐文件差异。桌面MSBuild默认AL路径未使用确定性生成；启用SDK已有 `GenerateSatelliteAssembliesForCore=true` 后，输出丢失和失败恢复的完整字节比较通过，保留原摘要门禁。
- 资源／结构门禁仅在完整源内容及Compile登记一致时复用，实际交付资源始终重查。失败或取消不新增成功记录；缓存不可核验时回到官方链。
- 待测：交互式 VS 快速生成、完整产品鼠标／键盘及像素、DPI／高对比度／文字缩放、Windows11 21H2、ARM64实机、无Windows App Runtime的隔离机器及干净系统的完整安装／启动。未卸载本机框架来模拟缺依赖。
- 本版提交不包含Ext／Stock源码改动，保持52cda0f原有布局；工作区原有的 `MenuQuery.cpp` 空行及 `Program.cs` 尾随空格保留、不纳入本版提交。

按版本本地提交，不推送。计划见 [Plan-0.0.24.0.md](Plan-0.0.24.0.md)。