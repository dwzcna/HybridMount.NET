# HybridMount 0.0.9.7 验收记录

2026-11-06，Windows x64。[PR #2](https://github.com/dwzcna/HybridMount.NET/pull/2)：上传指数退避与Retry-After。本次为逐个合并的第2/15项，独立递增第四段版本；不推送。

## 合并内容

- 来源：codex/pr-2-upload-transient-backoff，提交39e56798e3df3e3fe797e8703d8f5aee1c046560。合并前master为b83bebd98ddec8432b24410fec50e38c55b05602；三方结果先经受检补丁应用，再统一程序集、清单和README版本。
- 退避、下一次尝试时间及失败计数随旧兼容JSON持久化；显式重试可提前唤醒，成功重置。集成时修正服务器Retry-After长于五分钟被截短的问题：五分钟仅约束客户端指数退避，服务器更长等待完整保留，并更新确定性回归；补齐公共规则和任务所有者中文说明。
首轮针对性失败证据保留于pr02-targeted-first-failed.log／trx；原因是测试直接改库后未唤醒事件驱动调度器，已通过现有实例级调度入口显式唤醒，不降低条件写和未知结果断言。
- 保留三个工程、正式配置及同步根；现有0.0.9.5进程继续运行，不用本轮中间构建替换或强杀。

## 实际自动验证

| 项目 | 结果 | 证据（artifacts/validation） |
| --- | --- | --- |
| x64最小WinUI／原生 | 通过 | pr02-minimal.log |
| 针对性 | 76/76，零失败／跳过 | pr02-targeted.log／trx |
| Debug完整 | 653/653，零失败／跳过 | pr02-debug.log／trx |
| Release完整 | 653/653，零失败／跳过 | pr02-release.log／trx |
| x64／ARM64 MSIX | 签名及内容核验通过 | pr02-release.log、pr02-arm64-package.log |
| ARM64测试 | 仅交叉编译通过，无实机运行 | pr02-arm64-tests-build.log |

回归使用本地协议夹具与隔离NTFS／CFAPI根，不访问已取消NAS、不创建真实远端测试目录。未执行GUI／Explorer目视、真实服务兼容性、安装升级和ARM64实机，保留待验收；历史Shell和其他审查待办保持。

## 产物

| 架构 | 路径 | SHA256 |
| --- | --- | --- |
| x64 | artifacts/packages/x64/HybridMount_0.0.9.7_x64_Test/HybridMount_0.0.9.7_x64.msix | DC965A819C163D8B75BB6E72D7F6BA8218100823BB85C8546E5CC0091990FCBE |
| ARM64 | artifacts/packages/ARM64/HybridMount_0.0.9.7_arm64_Test/HybridMount_0.0.9.7_arm64.msix | 3F266729E1DBE7460A363643FB635FDBD1B4EAACADEE41064A207B97841002D9 |

汇总见 Validation-PR-Merges.md；机器可读证据为artifacts/validation/pr02-evidence.json。
