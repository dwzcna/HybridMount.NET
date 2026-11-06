# HybridMount 0.0.9.10 验收记录

2026-11-06，Windows x64。[PR #5](https://github.com/dwzcna/HybridMount.NET/pull/5)：S3目录枚举效率与坏条目隔离。本次为逐个合并的第5/15项，独立递增第四段版本；不推送。

## 合并内容

- 来源：codex/pr-5-s3-list-efficiency，提交9c3f4933058a0f7b0f996281ed6515dcff14115c。合并前master为2588ee1624880a4031ef9045de9c5ef09b39d2a8；三方结果先经受检补丁应用，再统一程序集、清单和README版本。
- S3目录列表使用分页返回元数据，移除逐文件HEAD；分页完成和游标校验保持，坏条目单独报告。版本比较兼容列表缺少VersionId，移动前重新核实源对象的真实版本；读取和写入仍执行各自的ETag／VersionId保护，补齐枚举与版本契约说明。
- 保留三个工程、正式配置及同步根；现有0.0.9.5进程继续运行，不用本轮中间构建替换或强杀。

## 实际自动验证

| 项目 | 结果 | 证据（artifacts/validation） |
| --- | --- | --- |
| x64最小WinUI／原生 | 通过 | pr05-minimal.log |
| 针对性 | 51/51，零失败／跳过 | pr05-targeted.log／trx |
| Debug完整 | 719/719，零失败／跳过 | pr05-debug.log／trx |
| Release完整 | 719/719，零失败／跳过 | pr05-release.log／trx |
| x64／ARM64 MSIX | 签名及内容核验通过 | pr05-release.log、pr05-arm64-package.log |
| ARM64测试 | 仅交叉编译通过，无实机运行 | pr05-arm64-tests-build.log |

回归使用本地协议夹具与隔离NTFS／CFAPI根，不访问已取消NAS、不创建真实远端测试目录。未执行GUI／Explorer目视、真实服务兼容性、安装升级和ARM64实机，保留待验收；历史Shell和其他审查待办保持。

## 产物

| 架构 | 路径 | SHA256 |
| --- | --- | --- |
| x64 | artifacts/packages/x64/HybridMount_0.0.9.10_x64_Test/HybridMount_0.0.9.10_x64.msix | 467A4C5EC1B009E13FE5F694ECBCC8127E4D01C8B6761A199E038A92A62275BF |
| ARM64 | artifacts/packages/ARM64/HybridMount_0.0.9.10_arm64_Test/HybridMount_0.0.9.10_arm64.msix | 2A1943A38031889B60A8B36C3BCABCA077725BF36EC922AD3D226525164BB2D7 |

汇总见 Validation-PR-Merges.md；机器可读证据为artifacts/validation/pr05-evidence.json。
