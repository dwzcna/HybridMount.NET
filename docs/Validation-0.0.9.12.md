# HybridMount 0.0.9.12 验收记录

2026-11-06，Windows x64。[PR #7](https://github.com/dwzcna/HybridMount.NET/pull/7)：S3分片日志与隔离恢复。本次为逐个合并的第7/15项，独立递增第四段版本；不推送。

## 合并内容

- 来源：codex/pr-7-s3-multipart-journal，提交47069f77acf5c751dca1ed81f477a20d1eeda659。合并前master为eba8cc53f5a7637acddab76b9a60406e99e40f55；三方结果先经受检补丁应用，再统一程序集、清单和README版本。
- 初始化前持久写入意图、记录UploadId后按精确ID恢复，损坏记录隔离且逐条失败不阻挡读取。对象树冲突同时保留PR5语义版本比较。集成复现发现原时间窗猜测会中止同键外部上传（pr07-foreign-before及details证据）；已取消这类猜测，无UploadId意图保留待核对并限频告警，已知UploadId仍独立恢复。无ID自动精确回收未实现，列入未完成；不将时间相近视作所有权。
- 保留三个工程、正式配置及同步根；现有0.0.9.5进程继续运行，不用本轮中间构建替换或强杀。

## 实际自动验证

| 项目 | 结果 | 证据（artifacts/validation） |
| --- | --- | --- |
| x64最小WinUI／原生 | 通过 | pr07-minimal.log |
| 针对性 | 41/41，零失败／跳过 | pr07-targeted.log／trx |
| Debug完整 | 748/748，零失败／跳过 | pr07-debug.log／trx |
| Release完整 | 748/748，零失败／跳过 | pr07-release.log／trx |
| x64／ARM64 MSIX | 签名及内容核验通过 | pr07-release.log、pr07-arm64-package.log |
| ARM64测试 | 仅交叉编译通过，无实机运行 | pr07-arm64-tests-build.log |

回归使用本地协议夹具与隔离NTFS／CFAPI根，不访问已取消NAS、不创建真实远端测试目录。未执行GUI／Explorer目视、真实服务兼容性、安装升级和ARM64实机，保留待验收；历史Shell和其他审查待办保持。

## 产物

| 架构 | 路径 | SHA256 |
| --- | --- | --- |
| x64 | artifacts/packages/x64/HybridMount_0.0.9.12_x64_Test/HybridMount_0.0.9.12_x64.msix | 59F0895B1030E6CF5683A651FD11DB168370C27AF99FEB4184FB260C16E8CB0F |
| ARM64 | artifacts/packages/ARM64/HybridMount_0.0.9.12_arm64_Test/HybridMount_0.0.9.12_arm64.msix | E76A1F0CFF39E97954D1EF36295A8198014115673BE40AE32D9F6B556492B06B |

汇总见 Validation-PR-Merges.md；机器可读证据为artifacts/validation/pr07-evidence.json。
