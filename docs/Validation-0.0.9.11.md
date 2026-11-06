# HybridMount 0.0.9.11 验收记录

2026-11-06，Windows x64。[PR #6](https://github.com/dwzcna/HybridMount.NET/pull/6)：S3分片条件完成与条件PUT回退。本次为逐个合并的第6/15项，独立递增第四段版本；不推送。

## 合并内容

- 来源：codex/pr-6-s3-multipart-fallback，提交f9e6a8ed21979e96c6cbefd4d2cddca2bc62b26b。合并前master为1e100fde56157ccb68031a98212e0bdc2d7aaa90；三方结果先经受检补丁应用，再统一程序集、清单和README版本。
- 对象树中解决传输字段冲突，同时保留PR3读取重试与本PR无进展超时。条件分片完成不支持时只在已验证条件PUT且文件不超过5 GiB的范围回退；更大内容拒绝。上传／下载流按120秒无进展计时，持续进度可继续，借用流不被接管，调用方取消保持。
- 保留三个工程、正式配置及同步根；现有0.0.9.5进程继续运行，不用本轮中间构建替换或强杀。

## 实际自动验证

| 项目 | 结果 | 证据（artifacts/validation） |
| --- | --- | --- |
| x64最小WinUI／原生 | 通过 | pr06-minimal.log |
| 针对性 | 20/20，零失败／跳过 | pr06-targeted.log／trx |
| Debug完整 | 729/729，零失败／跳过 | pr06-debug.log／trx |
| Release完整 | 729/729，零失败／跳过 | pr06-release.log／trx |
| x64／ARM64 MSIX | 签名及内容核验通过 | pr06-release.log、pr06-arm64-package.log |
| ARM64测试 | 仅交叉编译通过，无实机运行 | pr06-arm64-tests-build.log |

回归使用本地协议夹具与隔离NTFS／CFAPI根，不访问已取消NAS、不创建真实远端测试目录。未执行GUI／Explorer目视、真实服务兼容性、安装升级和ARM64实机，保留待验收；历史Shell和其他审查待办保持。

## 产物

| 架构 | 路径 | SHA256 |
| --- | --- | --- |
| x64 | artifacts/packages/x64/HybridMount_0.0.9.11_x64_Test/HybridMount_0.0.9.11_x64.msix | 8445354A7AD7EC15E6DE2ECACCCCD8C4E631343B4AE853886D803ECB991AEA95 |
| ARM64 | artifacts/packages/ARM64/HybridMount_0.0.9.11_arm64_Test/HybridMount_0.0.9.11_arm64.msix | 9F12074D08B8EE1A9BDB4D895F3B27588B14977E0101DF55EB4BA05138C5DD34 |

汇总见 Validation-PR-Merges.md；机器可读证据为artifacts/validation/pr06-evidence.json。
