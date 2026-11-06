# HybridMount 0.0.9.16 验收记录

2026-11-06，Windows x64。[PR #11](https://github.com/dwzcna/HybridMount.NET/pull/11)：本地上传索引与调度唤醒。本次为逐个合并的第11/15项，独立递增第四段版本；不推送。

## 合并内容

- 来源：codex/pr-11-incremental-local-scan，提交2d027c06b3e6af17f816981376894178c95f6ab8。合并前master为1f55d0353093f2d4b7fcbfa0acdf490410b38f78；三方结果先经受检补丁应用，再统一程序集、清单和README版本。
- 一次枚举构造路径／ID／同名替换索引和删除祖先树，保持原有首项顺序、大小写、歧义及远端删除保护规则。调度Wake不再触发全扫，Watcher及CFAPI变化独立RequestScan；与PR2到期事件等待和PR10锁外准备兼容，补齐索引及线程边界说明。随机对照及两万项操作量测试分别验证语义和查询范围，不据此宣称真实大库性能认证。
- 保留三个工程、正式配置及同步根；本批未切换、重启或强杀既有0.0.9.5程序，不安装本轮中间构建。

## 实际自动验证

| 项目 | 结果 | 证据（artifacts/validation） |
| --- | --- | --- |
| x64最小WinUI／原生 | 通过 | pr11-minimal.log |
| 针对性 | 88/88，零失败／跳过 | pr11-targeted.log／trx |
| Debug完整 | 826/826，零失败／跳过 | pr11-debug.log／trx |
| Release完整 | 826/826，零失败／跳过 | pr11-release.log／trx |
| x64／ARM64 MSIX | 签名及内容核验通过 | pr11-release.log、pr11-arm64-package.log |
| ARM64测试 | 仅交叉编译通过，无实机运行 | pr11-arm64-tests-build.log |

回归使用本地协议夹具与隔离NTFS／CFAPI根，不访问已取消NAS、不创建真实远端测试目录。未执行GUI／Explorer目视、真实服务兼容性、安装升级和ARM64实机，保留待验收；历史Shell和其他审查待办保持。

## 产物

| 架构 | 路径 | SHA256 |
| --- | --- | --- |
| x64 | artifacts/packages/x64/HybridMount_0.0.9.16_x64_Test/HybridMount_0.0.9.16_x64.msix | F3A8A687CBD81624ADA8EC34E6BE1F35986DF47D0AF3FD98C8108EF229396E88 |
| ARM64 | artifacts/packages/ARM64/HybridMount_0.0.9.16_arm64_Test/HybridMount_0.0.9.16_arm64.msix | 9763EF97FFBE2C8DA8DE385A3E1D25F5BB43CE6D2614F3B2A121254E0B3D8AE6 |

汇总见 Validation-PR-Merges.md；机器可读证据为artifacts/validation/pr11-evidence.json。
