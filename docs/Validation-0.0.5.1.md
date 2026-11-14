# HybridMount 0.0.5.1 验收记录

日期：2026-10-31，Windows x64，.NET SDK 10.0.401。产品、程序集与 MSIX 均为 0.0.5.1。

## 已完成

- 每挂载默认关闭的实验缩略图设置、捕获挂载 ID 保存、刷新防重入与失败恢复；关闭取消并等待当前挂载排队／读取，缓存保留。
- JPEG、PNG、BMP、GIF 首帧、MP4/H.264 和 EXE 数据资源图标；JPEG EXIF 方向校正。独立同 EXE 工作模式，不创建主应用会话，不传递凭据。
- 严格预览接口与预算：8 MiB、32 次范围请求、10 秒、每挂载一个／全局两个；解码器限制 3200 万像素、512 MiB Job，四尺寸最长边不超过 256。
- IPC v2 的缓存查询与异步 requestPreview，原生处理器共用 150ms 截止时间；失败使用类型图标，不打开占位内容、不自动启动应用。
- 修正重启时同路径旧排队记录绕过已发送结果核对的问题，保留未知写入保护。

## 未完成

本版范围内无已知待实现代码。正式处理器自动注册继续受真实 Explorer 验收门控，实验 MSIX 可显式生成；退出收尾修复在 0.0.5.2 实施。

## 待测试

- 实际 Explorer 小图标、大图标、缩略图视图、预览窗格及 EXE 内嵌图标路径；GUI 开关切换／选择／保存失败的实际视觉交互；系统解码器组合和第三方扩展影响。
- 正式包暂不自动接管，使用实验包后每挂载仍需手动开启。直接 COM／IPC 测试不能证明 Explorer 自动选择。
- 安装升级、ARM64 实机、超大／损坏媒体和长期慢服务压力。GUI 工具初始化及重置重试失败，未执行输入；见 0051-gui-attempt.txt。
- 历史 WebDAV 精确残留 DELETE 409 沿用 0.0.5 的未解决记录，本轮没有扩大清理范围。

## 已测试

证据在 artifacts/validation，脚本、产物及本机凭据不提交。

| 范围 | 实际结果及证据 |
|---|---|
| x64 Debug／Release | 最终各 173/173，无跳过；0051-all-x64-*.trx |
| 预览管线 | 真实独立工作进程覆盖六种格式、JPEG 方向、窗口合并、预算耗尽、取消、开关默认及隔离、弱版本回退后本地成功、缓存版本／尺寸；PreviewPipelineTests |
| Shell 接口 | 真实原生 COM、独立管道异步 requestPreview、150ms 回退、旧服务兼容、普通复制／脱水／固定与 EXE 缓存回归；当时的预览身份QueueTests／PreviewTests |
| 真实 R2 | JPEG 与 MP4 由文件系统上传，读回 SHA-256；脱水后严格读取生成预览，原占位仍未完整水合；0051-real-preview.json |
| 真实坚果云 | JPEG 与 MP4 冷预览因版本不可靠回退，未水合；显式下载后生成本地缓存；同一记录内临时项为零 |
| 精确清理 | 四个本轮真实测试文件及两个隔离根全部清理，没有创建远端测试目录，原挂载未改绑 |
| 非打包启动 | 实际静默启动、重复实例、窗口恢复、非挂载拒绝通过；0051-launch-x64-Unpackaged.*。仅本轮进程受控结束，不代表托盘退出已验收 |
| 签名包 | x64／ARM64 正式及实验 MSIX 架构、资源、声明与签名通过；0051-signature-*、0051-preview-signature-* |

首次真实测试的工作进程定位受 PowerShell 宿主目录影响，已改为按测试程序集定位；首次失败文件已精确清理。Release 首次异常计数混入并行测试的预期冲突，改为隔离进程级监听。存储失败注入改用超过系统上限的键组件后通过；保留失败尝试，最终结果以本页 TRX 为准。

```powershell
./build/Build.ps1 -Platform x64 -Configuration Debug -Deployment Unpackaged -Test
./build/Build.ps1 -Platform x64 -Configuration Release -Package -Test -CertificateThumbprint '<当前用户证书指纹>'
./build/Build.ps1 -Platform ARM64 -Configuration Release -Package -CertificateThumbprint '<当前用户证书指纹>'
# 两架构分别串行生成实验包
./build/Build.ps1 -Platform x64 -Configuration Release -Package -ExperimentalThumbnails -CertificateThumbprint '<当前用户证书指纹>'
./build/Build.ps1 -Platform ARM64 -Configuration Release -Package -ExperimentalThumbnails -CertificateThumbprint '<当前用户证书指纹>'
```

| 架构／模式 | 签名包 | SHA-256 |
|---|---|---|
| x64／正式 | artifacts/packages/x64/HybridMount_0.0.5.1_x64_Test/HybridMount_0.0.5.1_x64.msix | 79A0695C61F377164E0AB3A5AFAEC9F978D6351EDE5B9A61E684E165392061F0 |
| ARM64／正式 | artifacts/packages/ARM64/HybridMount_0.0.5.1_arm64_Test/HybridMount_0.0.5.1_arm64.msix | 5513B35716837287E784EF0B7582199C3CD9B62FBE9A3D8F58ACED2EEA4B76DA |
| x64／实验 | artifacts/packages-preview/x64/HybridMount_0.0.5.1_x64_Test/HybridMount_0.0.5.1_x64.msix | E69D92520507EE2EEB54C8407E684E462053C70EE61EF38CA95AAC30404355A0 |
| ARM64／实验 | artifacts/packages-preview/ARM64/HybridMount_0.0.5.1_arm64_Test/HybridMount_0.0.5.1_arm64.msix | B77EB55347F9051A9540FBCD688FB492CFD2BF2553E22D7DDA65B424FECDF6CA |

证书为本机开发证书，未加时间戳；ARM64 仅交叉构建。
