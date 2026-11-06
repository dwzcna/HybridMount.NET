# HybridMount 0.0.9.9 验收记录

2026-11-06，Windows x64。[PR #4](https://github.com/dwzcna/HybridMount.NET/pull/4)：远端区间单请求流式读取。本次为逐个合并的第4/15项，独立递增第四段版本；不推送。

## 合并内容

- 来源：codex/pr-4-streaming-range-read，提交6fab3d0180f7a0d985cf3fdf64d96aa6a322d801。合并前master为c951fe58d5b9a36bce4b2317067258e2afccd943；三方结果先经受检补丁应用，再统一程序集、清单和README版本。
- 新增兼容的顺序范围读取接口；S3／WebDAV验证同一版本后以单请求分块交付，网络恢复保留未满尾块和已消费偏移，不重复消费者副作用。普通忽略Range仍走共享快照兼容路径，严格预览保持原入口；CFAPI、远端暂存及摘要核对接入流式读取，补齐缓冲与取消契约说明。
- 保留三个工程、正式配置及同步根；现有0.0.9.5进程继续运行，不用本轮中间构建替换或强杀。

## 实际自动验证

| 项目 | 结果 | 证据（artifacts/validation） |
| --- | --- | --- |
| x64最小WinUI／原生 | 通过 | pr04-minimal.log |
| 针对性 | 32/32，零失败／跳过 | pr04-targeted.log／trx |
| Debug完整 | 701/701，零失败／跳过 | pr04-debug.log／trx |
| Release完整 | 701/701，零失败／跳过 | pr04-release.log／trx |
| x64／ARM64 MSIX | 签名及内容核验通过 | pr04-release.log、pr04-arm64-package.log |
| ARM64测试 | 仅交叉编译通过，无实机运行 | pr04-arm64-tests-build.log |

回归使用本地协议夹具与隔离NTFS／CFAPI根，不访问已取消NAS、不创建真实远端测试目录。未执行GUI／Explorer目视、真实服务兼容性、安装升级和ARM64实机，保留待验收；历史Shell和其他审查待办保持。

## 产物

| 架构 | 路径 | SHA256 |
| --- | --- | --- |
| x64 | artifacts/packages/x64/HybridMount_0.0.9.9_x64_Test/HybridMount_0.0.9.9_x64.msix | A4208E070CFCB608D789919B0C517FC8539B14C461367BCFA13C51CDF0A6D302 |
| ARM64 | artifacts/packages/ARM64/HybridMount_0.0.9.9_arm64_Test/HybridMount_0.0.9.9_arm64.msix | EF26966CFA23B1612C92B03AF3194D95D26658D79679E3B1D6E87C8E816370D3 |

汇总见 Validation-PR-Merges.md；机器可读证据为artifacts/validation/pr04-evidence.json。
