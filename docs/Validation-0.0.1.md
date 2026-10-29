# 0.0.1 验证记录

日期：2026-10-29。代码实现与验收分开记录；“待测试”不表示未写代码。

## 已测试

| 类别 | 平台／结果 | 证据（相对 artifacts/validation） |
| --- | --- | --- |
| 自动化契约与原生集成 | x64 Debug、Release：11/11 通过 | contracts-x64.trx、contracts-x64-Release.trx |
| 解决方案 | HybridMount.slnx；x64 Debug（Packaged）、DebugUnpackaged 均构建通过 | solution-x64-Debug-Packaged.log、solution-x64-DebugUnpackaged.log |
| VS 启动配置 | MsixPackage 配置存在并通过 SDK VerifyLaunchSettings；两个构建配置分别为 MSIX／None | vs-launch-profiles.log；可用 msbuild -getProperty:WindowsPackageType 复核 |
| 非打包运行 | 用户确认可运行；静默启动、重复启动和实际进程 IPC 通过 | launch-x64-Unpackaged.txt |
| 主程序和原生扩展 | x64／ARM64 Release 编译通过 | native-<架构>-Release.log、managed-<架构>-Release-Packaged.log |
| MSIX | 双架构生成、自包含、原生扩展及资源齐全，PE 架构和声明验证通过 | build/Test-Package.ps1 输出及构建日志 |
| 签名 | 两个包使用当前用户已有 CN=dwzcn 证书签名；signtool verify /pa /all 通过 | signature-x64.log、signature-ARM64.log、package-<架构>.sha256.txt |
| 新资源 | 骡子图形已检查 256px 预览；导出 ICO 含 16–256 多尺寸，包中没有旧产品图形名称，全部缩放 PNG 尺寸检查通过；38 份 PNG／ICO 与参考资源的相同哈希数为 0 | HybridMount/Assets/HybridMount.svg、build/Update-Assets.ps1、new-assets.json |

11 项自动化用例直接链接生产服务，使用隔离注册表子键、临时 SQLite 数据库和临时测试文件，不依赖完整 WinUI 进程：

- 外观保存失败保留正式快照和 ViewModel 选择；读取失败禁止保存；无效字段独立回退且读操作不回写。
- 真实 SQLite 写队列有序执行、最近一百条读取、清除屏障与退出排空；重新打开能读到最后提交条目。
- 零长度、过长和 uint 最大值帧拒绝；Unicode 路径往返、截断检测、版本／操作／相对路径校验。
- 畸形客户端及故障订阅者之后服务仍工作；freeSpace／keepOnDevice 返回 notImplemented，原测试文件内容不变。
- 单实例互斥量的第二线程拒绝与释放后接管。
- 加载真实扩展 DLL，检查独立枚举器、Clone 位置、菜单标题、状态、COM 释放；原生客户端实际向托管服务发送两个文件命令，路径和操作正确。

实际 WinUI 进程验证另外执行了：`--tray` 首次启动没有可见主窗口；第二次 `--tray` 退出并保持隐藏；正常第二实例退出且恢复原窗口；文件占位请求返回尚未实现且测试内容不变。测试结束保留窗口供人工查看。这里的“无可见主窗口”根据进程窗口元数据判断，不能替代托盘图形或焦点的完整人工检查。

本机也曾观察到已注册的 0.0.1 x64 包 Status=Ok 且响应 ping。这不等同于最终签名 MSIX 的安装／升级验收。

## 待测试

- 最终签名 MSIX 的全新安装、同身份升级和卸载；此轮没有重装或卸载用户已有包。
- Explorer 实际文件／目录／目录空白处菜单、多选、应用未运行时由原生扩展拉起、失败提示与选中文件不变。
- 实际点击关闭隐藏、最小化保留任务栏、托盘双击与两项菜单、真正退出、Explorer 重启重建图标、托盘注册失败回退。
- 登录启动开关、下次 Windows 登录、用户在启动应用中禁用及系统策略；不以 `--tray` 测试替代系统登录验收。
- 主题全部选项、跟随系统切换、高对比度、材质不支持／透明效果关闭时回退、设置重启恢复与访问拒绝。
- 日志页布局、搜索／级别筛选、剪贴板、文件选择器导出、确认清除以及大消息显示。
- ARM64 实机安装、运行、Explorer 扩展和自动化测试；本机仅完成交叉编译与静态包检查。

桌面工具调用连续返回 `trusted Node process exited unexpectedly; kernel reset, rerun your request`；文件查看辅助也受沙箱初始化故障影响。本轮使用已授权的构建和进程／IPC 检查完成可执行验证，没有把这些结果写作完整视觉验收。

## 复现

先从托盘退出运行中的应用，避免占用原生集成测试所需的本用户管道：

```powershell
.\build\Build.ps1 -Platform x64 -Configuration Debug -Deployment Unpackaged -Test
.\build\Build.ps1 -Platform x64 -Configuration Release -Deployment Packaged -Package -Test
.\build\Build.ps1 -Platform ARM64 -Configuration Release -Deployment Packaged -Package

# 进程行为验证；执行后会保留主实例运行
.\build\Test-Launch.ps1 -ExecutablePath '.\HybridMount\bin\x64\DebugUnpackaged\Unpackaged\net10.0-windows10.0.26100.0\win-x64\HybridMount.NET.exe'
```

DebugUnpackaged 输出由 slnx 该配置构建生成；构建脚本的 Debug + Unpackaged 输出则位于 bin/x64/Debug/Unpackaged。二者程序相同，路径不同。

验证期间发现并修复：非法帧长度异常未被工作任务捕获；MSIX Verb Id 含不允许的点号；包验证读取 UTF-8 BOM；VS 缺少 launchSettings.json。修复后分别完成对应复测。
