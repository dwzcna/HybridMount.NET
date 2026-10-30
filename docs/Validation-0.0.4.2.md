# 0.0.4.2 验证与交付记录

日期：2026-10-30；Windows x64；产品、程序集、原生资源及 MSIX 均为 0.0.4.2。预计开发／技术验证／自动化 90–150 分钟，桌面环境和外部等待另计；本轮从 05:32 UTC 开始，约 100 分钟完成代码与必要验证，文档、提交和推送另计。

## 已完成

仅描述代码实现：

- CFAPI 回调同步复制进程信息，异步只保留标量；日志可记录 PID、进程文件名、范围、优先级、显式下载、排队、首块、传输量和整文件退化原因，不读取命令行。
- 整文件缓存及未知 Range 的首次协商共享工作，独立等待者取消；共享工作拥有缓冲和 HTTP 响应，最后等待者离开／挂载停止才取消。重叠元数据查询合并，不缓存完成的元数据结果。
- 四路并发排队，显式操作优先，使用 CFAPI 0–15 提示，每八次调度让最早请求先行；CFAPI 显式水合标志不被解释为“用户双击”。
- 按回调实例跟踪并行范围，兼容同一 TransferKey 和 RequestKey=0；取消按范围覆盖匹配，不再默默丢弃 File.Copy 的并行请求。
- IPC v2 getPreview 只接收一个本产品文件与 16–256 尺寸，返回可选 PNG；只读本机身份、索引和缓存，不联网、不入队、不自动启动提取。
- 冷占位在打开内容句柄前排除；完整、未修改且身份一致的本地 EXE，使用只读排他 oplock、保护引用和独立事件读取 PE 资源，不执行 EXE，也不用 FileStream 重新绑定 CFAPI 的完成端口。
- PE 资源表上限 16 MiB，单图标资源上限 4 MiB；PNG 上限 384 KiB，尺寸 32／64／128／256，按挂载／条目／原始版本／尺寸隔离，纳入缓存软上限。
- 提取与刷新、脱水、移除共用根锁；关闭资源句柄后，以 SHCNF_PATHW | SHCNF_FLUSHNOWAIT 通知 Shell，避免同步通知等待内容句柄。
- HybridMount.Ext 内实现 IInitializeWithItem／IThumbnailProvider 与 WIC 有界解码；冷缓存、旧服务、错误关联、坏 PNG、150 ms 管道截止时间或服务缺失时返回系统通用程序图标，不自动激活主程序；其他文件类型返回未实现。
- 正式 MSIX 默认省略云缩略图处理器自动注册，候选 COM 类保留。构建属性 HybridMountEnableCloudThumbnails=true 生成实验清单；SDK 26100 的 CloudFiles all 组要求其他空处理器元素一并存在，未为这些空项指定 CLSID。
- 原生测试使用单独 DLL、输出目录和带测试进程 ID 的管道；产品 DLL 不定义隔离测试宏。仍只有三个产品工程，配置／挂载／任务／凭据兼容原版本。

## 未完成

- 本轮已列代码范围内无已知待实现项。自动双向同步、自动写回与周期重扫仍属后续范围。

## 待测试

- **启用门槛尚未满足**：真实 Explorer 小图标、大图标、预览窗格、重复／多文件浏览、双击实际执行与 Explorer 复制，以及非 EXE 图标／缩略图展示。正式包不自动注册候选处理器，不能据本轮结果宣称所有大 EXE 浏览卡顿已消除。
- 已安装候选处理器时，系统各路径是否实际选取它、其他处理器是否正确回退，以及缓存更新通知的完整视觉效果。
- 签名 MSIX 安装、升级／卸载；ARM64 实机、GUI 操作和压力。交叉构建不是 ARM64 运行验收。
- 真实服务的大文件／弱验证器／网络抖动压力、上传和条件修改权限。真实服务本轮只读，S3 对象只核对前 64 KiB。
- 未覆盖路径若仍触发隐式内容读取，继续按实际路径定位；保留普通隐式读取以保障双击、复制及应用读取。弱／缺失验证器仍需完整快照，首个实际打开可能等待。

## 已测试

证据位于忽略目录 artifacts/validation；以下为最终代码的实际结果。

| 范围 | 结果 | 证据 |
| --- | --- | --- |
| x64 Debug／Unpackaged | WinUI、原生扩展编译成功；117/117 | managed-x64-Debug-Unpackaged.log、contracts-x64-Debug.trx |
| x64 Release／Packaged | WinUI、原生扩展编译成功；117/117 | managed-x64-Release-Packaged.log、contracts-x64-Release.trx |
| 共享工作 | 第一等待者取消互不误伤、最后取消、停止、重试、版本隔离和元数据合并通过 | ReadOptimizationTests |
| 范围协商 | 慢 HTTP、忽略 Range 的单 GET／响应所有权、弱／缺失标签的下载后版本变化拒绝、真实字节与原因诊断通过 | ReadOptimizationTests、WebDavTests |
| 优先级／回调 | 显式／系统提示、公平、取消队列和原生进程快照通过；真实 File.Copy 触发 RequestKey=0 的多范围读取并成功 | ReadOptimizationTests、PreviewTests |
| 16 MiB EXE | 可靠 Range、忽略 Range、弱标签、头／尾读取、S3 VersionId 范围保持旧快照通过；均未执行测试 EXE | LargeProtocolReadTests |
| 真实 CFAPI 图标闭环 | 冷查询零 GET；下载后 PNG 生成；请求 80 返回 128 像素缓存证明 WIC 实际解码；脱水后零下载；版本隔离、软上限回收、普通复制及只读资源提取通过 | PreviewTests、0042-concurrent-cf-ranges.trx |
| 兼容回退 | 旧版本、错误请求 ID、坏 PNG、慢服务回退通用图标且无水合；非 EXE 不查询；原生菜单独立管道往返通过 | PreviewTests、NativeExtensionTests |
| 系统 Shell API | 真实 IShellItem 绑定与 16／256 IShellItemImageFactory 返回成功，本机测量为 0 GET；没有启用候选处理器的自动选择，也不是 Explorer 窗口验收 | PreviewTests 标准输出、最终 TRX |
| 真实 WebDAV | 当前 DPAPI 配置探测成功；独立 NTFS 根占位，176,202 字节读取与直接快照一致；Range=Unsupported，完整文件缓存 | 0042-real-services.json、0042-real-webdav.log |
| 真实 S3 | 当前本机 HTTPS／Path-Style／us-east-1 配置探测成功；枚举、元数据、独立 CFAPI 占位，从 13,150,616 字节对象核对前 65,536 字节，Range=Supported；保护上限 1 MiB，未整件下载 | 0042-real-services.json、0042-real-s3.log |
| 非打包进程 | 实际静默启动、ping ready、重复启动退出 0、getPreview 非挂载拒绝且测试文件未变；新版实例保留托盘，未执行 GUI 输入 | 0042-launch-x64-Unpackaged.json |
| x64／ARM64 正式包 | 四段版本、自包含、PE 架构、资源／依赖、COM／菜单／CloudFiles／启动声明、默认无 ThumbnailProviderHandler；签名验证通过 | signature-x64.log、signature-ARM64.log、package-*.sha256.txt |
| 二进制版本 | Debug／Release 主程序、双架构扩展及 Release 测试六项均 0.0.4.2 | 0042-binary-versions.json |
| 实验清单 | 候选 ThumbnailProviderHandler 的 MSIX 模式及包内容检查通过，未安装；与正式关闭清单分开输出 | 0042-preview-experimental-build.log |
| 桌面工具 | 初始化发生 windows sandbox helper_unknown_error，重置后仍 trusted Node process exited unexpectedly；没有执行 GUI 输入 | 0042-gui-attempt.txt |
| 隔离现场清理 | 本轮 3 个超时测试根按创建区间、名称、绝对路径及原生身份核验后离线清空／注销；未处理用户挂载 | 0042-isolated-test-cleanup.log |

历史 S3 401 在当前保存配置下未再复现，不能由此确定旧失败原因或声称某个签名修改修复了认证。初次有界扫描没有找到 4 MiB 内的文件，随后改为只读较大对象的前 64 KiB，完成闭环；未向真实桶上传测试文件。

Release 曾发现首次 Range 能力发布和等待者交接之间的竞态，已改为共享首次探测。普通复制进一步证实同一零 RequestKey 对应多个并发范围，已移除错误去重。修复后的最终 117 项双配置回归覆盖这些路径；失败尝试不是通过记录。

诊断开关为启动进程环境变量 `HYBRIDMOUNT_HYDRATION_DIAGNOSTICS=1`，默认关闭；开启后重新启动主程序，在日志页筛选“水合诊断”或“预览诊断”。日志不含命令行、授权头、密钥或原始服务响应。普通隐式读取继续允许。

正式签名产物：

- artifacts/packages/x64/HybridMount_0.0.4.2_x64_Test/HybridMount_0.0.4.2_x64.msix
- artifacts/packages/ARM64/HybridMount_0.0.4.2_arm64_Test/HybridMount_0.0.4.2_arm64.msix

构建入口及测试命令见 README；清单实验可用 MSBuild 的 `/p:HybridMountEnableCloudThumbnails=true`，它只用于启用门槛验收，不改变正式包默认值。

参考资料：

- [CF_CALLBACK_INFO](https://learn.microsoft.com/en-us/windows/win32/api/cfapi/ns-cfapi-cf_callback_info)
- [CF_CALLBACK_PARAMETERS](https://learn.microsoft.com/en-us/windows/win32/api/cfapi/ns-cfapi-cf_callback_parameters)
- [CfOpenFileWithOplock](https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfopenfilewithoplock)
- [云缩略图清单](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-desktop3-thumbnailproviderhandler)
- [SHChangeNotify](https://learn.microsoft.com/en-us/windows/win32/api/shlobj_core/nf-shlobj_core-shchangenotify)
