# HybridMount 0.0.9.8 验收记录

2026-11-06，Windows x64。[PR #3](https://github.com/dwzcna/HybridMount.NET/pull/3)：S3只读请求有限重试。本次为逐个合并的第3/15项，独立递增第四段版本；不推送。

## 合并内容

- 来源：codex/pr-3-s3-read-retry，提交d71cbfa8ab28cec4674db924541779cef4cb459c。合并前master为48ecc5660ae51c0673e6cdfabd05e2319cb43b27；三方结果先经受检补丁应用，再统一程序集、清单和README版本。
- S3只读请求最多三次短退避重试，每次继续验证原范围、长度、ETag与VersionId；调用方取消立即停止，证书／认证／版本冲突不重试。修改请求仍单次发送并保留未知结果分类；补齐实例级重试设置和分类说明。
- 保留三个工程、正式配置及同步根；现有0.0.9.5进程继续运行，不用本轮中间构建替换或强杀。

## 实际自动验证

| 项目 | 结果 | 证据（artifacts/validation） |
| --- | --- | --- |
| x64最小WinUI／原生 | 通过 | pr03-minimal.log |
| 针对性 | 56/56，零失败／跳过 | pr03-targeted.log／trx |
| Debug完整 | 681/681，零失败／跳过 | pr03-debug.log／trx |
| Release完整 | 681/681，零失败／跳过 | pr03-release.log／trx |
| x64／ARM64 MSIX | 签名及内容核验通过 | pr03-release.log、pr03-arm64-package.log |
| ARM64测试 | 仅交叉编译通过，无实机运行 | pr03-arm64-tests-build.log |

回归使用本地协议夹具与隔离NTFS／CFAPI根，不访问已取消NAS、不创建真实远端测试目录。未执行GUI／Explorer目视、真实服务兼容性、安装升级和ARM64实机，保留待验收；历史Shell和其他审查待办保持。

## 产物

| 架构 | 路径 | SHA256 |
| --- | --- | --- |
| x64 | artifacts/packages/x64/HybridMount_0.0.9.8_x64_Test/HybridMount_0.0.9.8_x64.msix | 7746387D3DEA0BFDBCC6443669C02F195CA67E7CBCFBB86D9C2E59A98C235553 |
| ARM64 | artifacts/packages/ARM64/HybridMount_0.0.9.8_arm64_Test/HybridMount_0.0.9.8_arm64.msix | 09C6E4ABA5B778CE1B63AA82D7A521DCD92269C87C08F3458AE8BF0FE08EB7C7 |

汇总见 Validation-PR-Merges.md；机器可读证据为artifacts/validation/pr03-evidence.json。
