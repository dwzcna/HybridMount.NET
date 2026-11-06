# HybridMount 0.0.9.23 验收记录

2026-11-06，Windows x64。[PR #18](https://github.com/dwzcna/HybridMount.NET/pull/18)：冲突副本使用不可变快照。本次为本轮继续逐个合并的第3/3项（累计PR #18），独立递增第四段版本；不推送master。

## 合并内容

- 来源：codex/pr-18-conflict-copy-snapshot，提交abb99e76c5552d9032e05afb7b49fb8b22345e83。合并前master为076a2744d60d0b7a3d584399815de7ee7128f765；三方结果先经受检补丁应用，再统一程序集、清单和README版本。
- PR #18 将冲突副本绑定至已持久化的不可变快照：后续原路径改写或删除仍发送保存的内容A，快照缺失／无效拒绝且不回退新内容；无既有快照时界面明确使用准备时当前内容。仓库锁协调共享快照引用与回收，副本发送不将原文件标记同步。补齐中文职责说明，并新增真实隔离NTFS／CFAPI及本地DAV工作器回归，覆盖原文件改写、删除及同长度快照损坏；验证If-None-Match:*、零原路径PUT、原内容／状态保护。自动验证仅使用本地夹具，未连接真实服务、未操作正式挂载。
- 保留三个工程、正式配置及同步根；本批未切换、重启或强杀既有0.0.9.5程序，不安装本轮中间构建。

## 实际自动验证

| 项目 | 结果 | 证据（artifacts/validation） |
| --- | --- | --- |
| x64最小WinUI／原生 | 通过 | pr18-minimal.log |
| 针对性 | 92/92，零失败／跳过 | pr18-targeted.log／trx |
| Debug完整 | 1033/1033，零失败／跳过 | pr18-debug.log／trx |
| Release完整 | 1033/1033，零失败／跳过 | pr18-release.log／trx |
| x64／ARM64 MSIX | 签名及内容核验通过 | pr18-release.log、pr18-arm64-package.log |
| ARM64测试 | 仅交叉编译通过，无实机运行 | pr18-arm64-tests-build.log |

回归使用本地协议夹具与隔离NTFS／CFAPI根，不访问已取消NAS、不创建真实远端测试目录。未执行GUI／Explorer目视、真实服务兼容性、安装升级和ARM64实机，保留待验收；历史Shell和其他审查待办保持。

## 产物

| 架构 | 路径 | SHA256 |
| --- | --- | --- |
| x64 | artifacts/packages/x64/HybridMount_0.0.9.23_x64_Test/HybridMount_0.0.9.23_x64.msix | 572B6001993F4C12053B8D216211529472EF97D83E74F8018D9B77F3DEEAA755 |
| ARM64 | artifacts/packages/ARM64/HybridMount_0.0.9.23_arm64_Test/HybridMount_0.0.9.23_arm64.msix | 718413675641EE2C2BE7256E45A4BD5C19963CA66A7F96F4A4A2999D8F0E58FF |

汇总见 Validation-PR-Merges.md；机器可读证据为artifacts/validation/pr18-evidence.json。
