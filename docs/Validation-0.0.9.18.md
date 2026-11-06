# HybridMount 0.0.9.18 验收记录

2026-11-06，Windows x64。[PR #13](https://github.com/dwzcna/HybridMount.NET/pull/13)：S3移动删除的语义版本比较。本次为逐个合并的第13/15项，独立递增第四段版本；不推送。

## 合并内容

- 来源：codex/pr-13-s3-move-version-comparison，提交f503aff05f1ddf0d7f2ea7374f688f0175351843。合并前master为43421128beb121067a0a89b33540bd66bc8282da；三方结果先经受检补丁应用，再统一程序集、清单和README版本。
- 模型冲突保留PR5已统一的可靠ETag／双方可用VersionId比较以及PR7新增的初始化意图字段，未引入第二套比较实现。删除和移动使用语义版本；兼容版本JSON字段顺序、可选VersionId出现／缺失及旧索引，不把ETag解释为MD5，条件删除不指定历史VersionId。源在复制后变化、目标回执核对及未确认写入保护继续生效。
首轮三项测试仅HEAD数量断言不兼容PR5复制前重新核实真实源版本，已更新为保留该请求的四次精确期待；If-Match、版本变化拒绝及不删历史版本的断言均保持，失败证据保留。
- 保留三个工程、正式配置及同步根；本批未切换、重启或强杀既有0.0.9.5程序，不安装本轮中间构建。

## 实际自动验证

| 项目 | 结果 | 证据（artifacts/validation） |
| --- | --- | --- |
| x64最小WinUI／原生 | 通过 | pr13-minimal.log |
| 针对性 | 58/58，零失败／跳过 | pr13-targeted.log／trx |
| Debug完整 | 880/880，零失败／跳过 | pr13-debug.log／trx |
| Release完整 | 880/880，零失败／跳过 | pr13-release.log／trx |
| x64／ARM64 MSIX | 签名及内容核验通过 | pr13-release.log、pr13-arm64-package.log |
| ARM64测试 | 仅交叉编译通过，无实机运行 | pr13-arm64-tests-build.log |

回归使用本地协议夹具与隔离NTFS／CFAPI根，不访问已取消NAS、不创建真实远端测试目录。未执行GUI／Explorer目视、真实服务兼容性、安装升级和ARM64实机，保留待验收；历史Shell和其他审查待办保持。

## 产物

| 架构 | 路径 | SHA256 |
| --- | --- | --- |
| x64 | artifacts/packages/x64/HybridMount_0.0.9.18_x64_Test/HybridMount_0.0.9.18_x64.msix | 4CA27B76B3F6CC39C055DF624CCF910AD32D6D9D965927BDAE3E33AAB33D564B |
| ARM64 | artifacts/packages/ARM64/HybridMount_0.0.9.18_arm64_Test/HybridMount_0.0.9.18_arm64.msix | 2A8920A7F27659EA8FC1CB386E82AF3CE89B8CE53EB12E62B1767F1F3F85167C |

汇总见 Validation-PR-Merges.md；机器可读证据为artifacts/validation/pr13-evidence.json。
