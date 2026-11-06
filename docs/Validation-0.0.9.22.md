# HybridMount 0.0.9.22 验收记录

2026-11-06，Windows x64。[PR #17](https://github.com/dwzcna/HybridMount.NET/pull/17)：暂存磁盘空间检查。本次为本轮继续逐个合并的第2/3项（累计PR #17），独立递增第四段版本；不推送master。

## 合并内容

- 来源：codex/pr-17-staging-disk-space，提交92dcea580e59772f9d06b8afd5206fb16fac4d8b。合并前master为3233db6f07ec80d5c6f8a680e18debb30aeabb4c；三方结果先经受检补丁应用，再统一程序集、清单和README版本。
- 采用实际拉取的最新92dcea5（已合并GitHub master），上传快照和远端暂存前按目标最近存在祖先卷检查所需正文加64 MiB余量；查询在根锁外，空间不足不建文件或发起正文请求。上传按已持久NextAttemptUtc等待一分钟，远端同版本重复唤醒合并，显式重试与新版本不延后；旧快照、基线、未完成本地提交及未知结果保护保持。补齐预先取消先于查盘／建文件的入口和回归、职责说明。空间检查不预留容量，后续磁盘变化仍走既有IO失败收尾；不足及查询失败以注入测试验证，并实际调用Windows可用空间查询，未填满真实卷。
- 保留三个工程、正式配置及同步根；本批未切换、重启或强杀既有0.0.9.5程序，不安装本轮中间构建。

## 实际自动验证

| 项目 | 结果 | 证据（artifacts/validation） |
| --- | --- | --- |
| x64最小WinUI／原生 | 通过 | pr17-minimal.log |
| 针对性 | 115/115，零失败／跳过 | pr17-targeted.log／trx |
| Debug完整 | 1010/1010，零失败／跳过 | pr17-debug.log／trx |
| Release完整 | 1010/1010，零失败／跳过 | pr17-release.log／trx |
| x64／ARM64 MSIX | 签名及内容核验通过 | pr17-release.log、pr17-arm64-package.log |
| ARM64测试 | 仅交叉编译通过，无实机运行 | pr17-arm64-tests-build.log |

回归使用本地协议夹具与隔离NTFS／CFAPI根，不访问已取消NAS、不创建真实远端测试目录。未执行GUI／Explorer目视、真实服务兼容性、安装升级和ARM64实机，保留待验收；历史Shell和其他审查待办保持。

## 产物

| 架构 | 路径 | SHA256 |
| --- | --- | --- |
| x64 | artifacts/packages/x64/HybridMount_0.0.9.22_x64_Test/HybridMount_0.0.9.22_x64.msix | DCCBADBA3E784944E57913F836841DB35B87A2BC6268348A1FDBEFAD5B832FB3 |
| ARM64 | artifacts/packages/ARM64/HybridMount_0.0.9.22_arm64_Test/HybridMount_0.0.9.22_arm64.msix | 9B904A227A4DD1CD826EC04A6B6295C28805243999D1E8F6138DC55B513E4245 |

汇总见 Validation-PR-Merges.md；机器可读证据为artifacts/validation/pr17-evidence.json。
