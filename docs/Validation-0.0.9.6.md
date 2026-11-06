# HybridMount 0.0.9.6 验收记录

2026-11-06，Windows x64。[PR #1](https://github.com/dwzcna/HybridMount.NET/pull/1)：发送前错误分类与条件写有限重试。本次为逐个合并的第1/15项，独立递增第四段版本；不推送。

## 合并内容

- 来源：codex/pr-1-upload-presend-retry，提交1e4188a58221ff83bdacb86b30766243b327cba7。合并前master为e5a63e26331e8687db87551d15387ceef00ecebb；三方结果先经受检补丁应用，再统一程序集、清单和README版本。
- 发送前能力探测先于Sending阶段；探测网络失败保持可恢复等待，已发送请求仍先核对远端。新增持久重试计数兼容旧JSON；仅已验证条件写在安全核对后有限重排，取消、版本冲突和已确认远端删除继续保护。补齐新增接口与协议入口的中文职责说明。
- 保留三个工程、正式配置及同步根；现有0.0.9.5进程继续运行，不用本轮中间构建替换或强杀。

## 实际自动验证

| 项目 | 结果 | 证据（artifacts/validation） |
| --- | --- | --- |
| x64最小WinUI／原生 | 通过 | pr01-minimal.log |
| 针对性 | 52/52，零失败／跳过 | pr01-targeted.log／trx |
| Debug完整 | 617/617，零失败／跳过 | pr01-debug.log／trx |
| Release完整 | 617/617，零失败／跳过 | pr01-release.log／trx |
| x64／ARM64 MSIX | 签名及内容核验通过 | pr01-release.log、pr01-arm64-package.log |
| ARM64测试 | 仅交叉编译通过，无实机运行 | pr01-arm64-tests-build.log |

回归使用本地协议夹具与隔离NTFS／CFAPI根，不访问已取消NAS、不创建真实远端测试目录。未执行GUI／Explorer目视、真实服务兼容性、安装升级和ARM64实机，保留待验收；历史Shell和其他审查待办保持。

## 产物

| 架构 | 路径 | SHA256 |
| --- | --- | --- |
| x64 | artifacts/packages/x64/HybridMount_0.0.9.6_x64_Test/HybridMount_0.0.9.6_x64.msix | 804B0B7A887258D501066972B9E1EF5102E587B9338331882245FD8DB42D48EC |
| ARM64 | artifacts/packages/ARM64/HybridMount_0.0.9.6_arm64_Test/HybridMount_0.0.9.6_arm64.msix | E48B40CB1FC5608DEA1095B8B994D32A79C1027879A0AB424F900799DBA3EA91 |

汇总见 Validation-PR-Merges.md；机器可读证据为artifacts/validation/pr01-evidence.json。
