# HybridMount 0.0.6 验收记录

日期：2026-11-02，Windows x64，.NET SDK 10.0.401、Visual Studio 2026 MSBuild 和 Windows SDK 10.0.26100.0。产品版本 0.0.6，程序集及 MSIX 为 0.0.6.0。范围为截图中的打包向导配置与旧版本显示；预算 45–75 分钟，GUI／实机等待另计。

## 已完成

- 为 HybridMount.slnx 中主程序的打包 Debug／Release 配置添加部署映射，覆盖 x64／ARM64；两个非打包配置不部署。保留三个工程、原生依赖及测试配置映射。
- Directory.Build.props 升为 0.0.6，源 Package.appxmanifest 同步为 0.0.6.0，主程序、扩展和测试统一规范为四段版本。
- 构建检查源清单与集中 BinaryVersion 一致；不再只在生成副本中替换旧版本。源清单供 VS 向导读取，后续升版也必须同步；未同步时在编译前明确失败。
- 保留打包／非打包输出隔离、正式缩略图注册门控、AWSSDK.S3、IPC v2 和数据库格式；未修改协议或运行时服务代码。

## 未完成

打包配置与版本修复已实现。SHELL-01 的原生预览／Shell 阻塞原因和收尾修复尚未完成，F04–F11 及既有协议待办保持原边界，不在本轮修改。

## 待测试

- 实际 Visual Studio“创建应用程序包”向导：重新加载 HybridMount.slnx，核对版本 0.0.6.0，以及 x64／ARM64 的 Release 配置可选并实际创建包。配置解析与命令行打包已验证，未将它们记为向导 GUI 通过。
- SHELL-01：定位并复测下表 11 项失败，验证预览返回、独立管道释放及真实 Explorer 行为。首个失败的上版复现不能代替全部失败逐项归因。
- 签名 MSIX 安装升级、正常托盘退出及 ARM64 实机；本轮 ARM64 为交叉构建。
- Computer Use 初始化和重试均返回 `trusted Node process exited unexpectedly; kernel reset, rerun your request`，没有进行 GUI 输入或使用 PowerShell UI Automation 替代。

## 已测试

| 范围 | 实际结果与证据（artifacts/validation） |
| --- | --- |
| 向导配置筛选 | 本机 VS 实现要求 SolutionContext 同时 ShouldBuild、ShouldDeploy 并匹配平台；没有有效配置时隐藏 ARM64。微软 SolutionPersistence 公开模型核验全部 24 个项目映射，修复前可选 0 个、修复后可选 4 个；006-solution-configurations.json |
| 版本一致性保护 | 使用集中 Version=0.0.6.1、源清单 0.0.6.0 执行 PrepareProductManifest，明确返回版本不一致错误；源清单没有被构建改写。006-version-mismatch.log |
| 解决方案构建 | 直接使用 HybridMount.slnx，x64 Release 与 DebugUnpackaged 均通过；006-solution-release-x64.log、006-solution-debugunpackaged-x64.log |
| 完整自动化 | x64 Debug／Release 各 236 项：225 通过、11 失败、0 跳过，未改测试或隐藏失败；006-debug-first.trx、006-release-first.trx |
| 隔离及上版复核 | 当前版本单独运行四个预览回退用例仍为 0/4；从未修改的 HEAD 9891582 导出 0.0.5.3，独立还原、编译后也复现首个 30 秒超时及后三项管道占用，结果 0/4。006-preview-recheck.trx、006-baseline-preview.trx；具体阻塞原因待定位 |
| 实际非打包运行 | Ready、无窗口静默启动、静默重复实例、普通重复实例恢复窗口、非挂载缓存请求拒绝通过；006-launch-x64-Unpackaged.json／txt／log。无在途上传后仅结束本轮原始进程对象，受控中断不代表正常退出码 0 或托盘退出通过 |
| 原挂载保护 | 启动前后只读核对 D:\wdavtest、D:\s3test 的根身份与归属；无待上传任务或本地改动。未创建远端测试数据，未读取凭据；006-original-roots-before.json、006-original-roots-after.json |
| 失败测试收尾 | 本轮两个完整测试宿主、隔离复测及上版复核留下六个本地模拟根；按精确路径、挂载 ID、根归属和回环服务地址核对，通过公开 clearAndRemove 离线清空并注销。未调用 StartAsync／ConnectAsync，无远端协议客户端；006-local-test-cleanup.json、006-baseline-local-test-cleanup.json。独立测试目录及数据库保留作证据 |
| 版本与正式包 | x64／ARM64 主程序、原生扩展、测试版本为 0.0.6.0；双架构正式自包含 MSIX 的版本、PE 架构、资源、声明与签名通过，包内 DLL 与最终构建输出相同；006-binary-versions.json、006-final-packages.json、006-release-*-package.log |

11 项失败分组如下；未认定打包配置变更引发运行时代码回归，也未将它们直接认定为环境误报。

| 用例 | 数量 | 实际失败 |
| --- | --- | --- |
| PreviewTests.UnavailablePreviewDataFallsBackWithoutHydration | 4 | 首项 wrongId 30 秒超时，后三项管道实例被占用；这组在 0.0.5.3 基线同样复现 |
| PreviewTests.ActualShellBindingIsMeasuredSeparatelyFromIdentityOnlyHandler | 1 | Shell 绑定及初始化完成后 120 秒超时 |
| PreviewTests.ColdAndWarmCloudExePreviewsNeverHydrateAndLocalExtractionSurvivesDehydration | 1 | 隔离测试管道实例被占用 |
| PreviewTests.RealShellImageFactoryReportsAutomaticExeReads | 3 | 系统图像工厂调用 20 秒超时 |
| 当时的预览身份QueueTests.EnabledColdImageQueuesInBackgroundWhileNativeCallbackReturnsGenericIcon | 1 | 隔离测试管道实例被占用 |
| NativeExtensionTests.NativeComAndPipeRoundTrip | 1 | 隔离测试管道实例被占用 |

原始失败日志、TRX、基线源码和本机诊断辅助工具保留于忽略目录。没有更改 Defender 设置、运行先前被拦截的私有反射／凭据拼接网络命令，或把 SDK 已捕获异常认定为程序崩溃。

```powershell
# 直接验证 slnx 的打包和非打包构建
MSBuild.exe HybridMount.slnx -restore -m -p:Configuration=Release -p:Platform=x64 -p:GenerateAppxPackageOnBuild=false
MSBuild.exe HybridMount.slnx -restore -m -p:Configuration=DebugUnpackaged -p:Platform=x64 -p:GenerateAppxPackageOnBuild=false

# 完整回归：本轮实际各 225/236，失败明细见上表
./build/Build.ps1 -Platform x64 -Configuration Debug -Deployment Unpackaged -Test
./build/Build.ps1 -Platform x64 -Configuration Release -Deployment Unpackaged -Test

# 独立生成与签名正式包
./build/Build.ps1 -Platform x64 -Configuration Release -Package -CertificateThumbprint '<当前用户证书指纹>'
./build/Build.ps1 -Platform ARM64 -Configuration Release -Package -CertificateThumbprint '<当前用户证书指纹>'
```

本机开发证书签名，无时间戳；正式包保持实验缩略图处理器自动注册关闭。本轮没有更改实验注册逻辑，未重新生成实验包。

| 架构 | 签名 MSIX | SHA-256 |
| --- | --- | --- |
| x64 | artifacts/packages/x64/HybridMount_0.0.6.0_x64_Test/HybridMount_0.0.6.0_x64.msix | C8A95964C3794856218FFD2607F61204437E40F146DA4953C158B5F8AB92D9CF |
| ARM64 | artifacts/packages/ARM64/HybridMount_0.0.6.0_arm64_Test/HybridMount_0.0.6.0_arm64.msix | 17B56D87E0B0E8AC54547DD948F2684341001153B8AC7034703E693F3FE68F64 |
