# 0.0.2.1 验证记录

2026-10-29。原 0.0.2.1—0.0.2.3 合并交付；代码完成与实机验收独立记录。本轮开始于 14:48:28 UTC（北京时间 22:48:28）。

## 已完成代码

- 首页／挂载／任务／日志导航；任务 FontIcon E7C4，日志 FontIcon F120，挂载 MapDrive。
- 独立 TasksPage、进度／取消／逐项结果、任务选择恢复和页面订阅释放；挂载页不再承载任务列表。
- 新增挂载按钮、MenuFlyout 的模拟挂载项及 ContentDialog；名称、NTFS 目录、自动连接、防重复提交和失败提示。
- 下载并移除保留原有文件；清空并移除删除根内全部本地内容，保留空根目录与模拟源内容；取消为默认按钮。
- 按句柄、不跟随链接的清空；阻止新水合，结束在途读取并保留删除回调。模式与阶段持久保存，失败／取消不自动回滚或重放。
- 旧 JSON 缺失移除字段时默认保留文件；成功移除清理条目索引，保留任务历史。挂载 schema 为 2，SQLite 索引 schema 仍为 1。
- 版本 0.0.2.1 在程序集、原生资源、主程序显示及 MSIX 一致；三段输入补齐第四段，四段输入直接使用。IPC 继续 v2，清空仅在主界面提供。

## 已测试

| 场景 | 结果 | 证据（artifacts/validation 下） |
| --- | --- | --- |
| 清空专项，x64 Debug | 6/6 通过 | 0021-clear-x64-Debug.trx |
| 全量，x64 Debug | 25/25 通过，0 失败／跳过 | 0021-all-x64-Debug.trx |
| 全量，x64 Release | 25/25 通过，0 失败／跳过 | 0021-all-x64-Release.trx |
| x64 Debug Unpackaged | 编译通过 | 0021-managed-x64-Debug-Unpackaged.log |
| 实际非打包 WinUI 进程 | 静默启动、重复启动和拒绝非挂载请求通过 | 0021-launch-x64-Unpackaged.txt |
| x64 Release MSIX | 构建、签名、包内容及架构检查通过 | 0021-managed-x64-Release-Packaged.log、0021-signature-x64.log |
| ARM64 Release MSIX | 交叉构建、签名、包内容及架构检查通过 | 0021-managed-ARM64-Release-Packaged.log、0021-signature-ARM64.log |

6 项专项测试在独立 NTFS 目录和 HKCU 测试键内执行：

1. 旧版 Pending JSON 未包含新字段时，默认继续保留文件。
2. 清空未下载的大文件占位、本地修改和本地新增；测试期间暂时移走自有模拟源内容，清空仍成功。恢复源后核对对象身份、版本及尾部字节未变；外部目录联接被移除，目标文件不变；根目录身份保持不变，索引清理且历史保留。
3. 不共享删除的文件导致逐项失败，保留配置和清空模式；禁止切换为保留模式；重建宿主不会自动删除，解除占用后重试成功。
4. 首个删除完成后取消，已删内容保持删除，剩余内容及模式保留；重建宿主后显式重试成功。
5. 根身份不匹配时拒绝清空，没有删除项，原有目录内容不变。
6. 内容已清空、注销成功而配置尚未删除时，重建宿主并显式重试可完成收尾，空根目录保持存在。

原有 19 项仍覆盖配置、日志、帧边界、真实原生 COM／NamedPipe、源读写契约、CFAPI 占位／范围水合／固定／脱水、本地保护、重连及下载并移除。

启动脚本保留本轮进程 PID 40140 供手工验收；脚本通过进程状态和公开 IPC 验证，不代表页面视觉已验收。

## 产物

使用当前用户 CN=dwzcn 开发证书签名，指纹 3FB1D78271BA8F6EDD3CE163F57D93D13FEB83C0；两包均通过 signtool verify /pa /all /v。

| 架构 | 相对于项目根的路径 | SHA-256 |
| --- | --- | --- |
| x64 | artifacts/packages/x64/HybridMount_0.0.2.1_x64_Test/HybridMount_0.0.2.1_x64.msix | BB51FB61CDA57C63E75E46051E75909849A5A65279EAC51106690C1E70FB1899 |
| ARM64 | artifacts/packages/ARM64/HybridMount_0.0.2.1_arm64_Test/HybridMount_0.0.2.1_arm64.msix | 9CD69E6EDB78BE73FDE2BA5A354F035DCD2338D86AE66CBE7E3E0574C7523603 |

## 待测试

- 导航顺序和图标、任务信息实际显示、选择恢复、结果窗口及主题／缩放视觉。
- 新增按钮／菜单、目录选择器、创建校验、防重复提交及已保存但连接失败的 GUI 行为。
- 移除三个按钮、默认取消、具体路径与清空说明、未完成模式按钮限制的实际交互。
- 实际 Explorer、最终 MSIX 安装／从 0.0.2 升级／卸载及打包身份完整流程。
- ARM64 实机运行和 Explorer 扩展；交叉构建不作为实机通过。
- ACL 拒绝、持续并发新增／改名、目录替换竞争、大目录和断电恢复压力。

computer-use 初始化及恢复重试均返回 trusted Node process exited unexpectedly; kernel reset, rerun your request。未取得任何当前页面截图或点击证据，完整 GUI 保持待测试。

## 复现

先退出正在运行的应用，避免生产格式管道被原生集成测试占用。测试运行需本机 NTFS 和真实 Cloud Files 驱动，不降级为空文件模拟。

```powershell
.\build\Build.ps1 -Platform x64 -Configuration Debug -Deployment Unpackaged -Test
.\build\Build.ps1 -Platform x64 -Configuration Release -Deployment Packaged -Package -Test -CertificateThumbprint '<当前用户证书指纹>'
.\build\Build.ps1 -Platform ARM64 -Configuration Release -Deployment Packaged -Package -CertificateThumbprint '<当前用户证书指纹>'
.\build\Test-Launch.ps1 -ExecutablePath '.\HybridMount\bin\x64\Debug\Unpackaged\net10.0-windows10.0.26100.0\win-x64\HybridMount.NET.exe'
```

测试清理仅处理随机测试区域；无法完成注销时保留现场，不直接递归删除残余云文件。清空的取消不会恢复已删除内容。
