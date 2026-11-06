# HybridMount 0.0.9.17 验收记录

2026-11-06，Windows x64。[PR #12](https://github.com/dwzcna/HybridMount.NET/pull/12)：Watcher增量扫描与全量兜底。本次为逐个合并的第12/15项，独立递增第四段版本；不推送。

## 合并内容

- 来源：codex/pr-12-incremental-scan-queue，提交55133b54c5fe5cb8b378eb2c07cf164541c461a1。合并前master为b80c8cf40c097ed5c8b3988705e7928737b1e77a；三方结果先经受检补丁应用，再统一程序集、清单和README版本。
- 在对象树中解决PR2事件驱动等待与Watcher队列冲突，保留任务到期／Retry-After选择，移除旧scanned状态及500ms轮询。队列提供下一扫描截止，综合200ms静默、2s最大事件合并、2s失败补扫和60s兜底；单次事件没有第二次通知仍会按时扫描，新增实际超时等待回归。范围内缺失取消、重命名两端、溢出／大小写重命名全扫、扫描期间新事件及失败重扫均保持，范围外任务不取消。
- 保留三个工程、正式配置及同步根；本批未切换、重启或强杀既有0.0.9.5程序，不安装本轮中间构建。

## 实际自动验证

| 项目 | 结果 | 证据（artifacts/validation） |
| --- | --- | --- |
| x64最小WinUI／原生 | 通过 | pr12-minimal.log |
| 针对性 | 114/114，零失败／跳过 | pr12-targeted.log／trx |
| Debug完整 | 852/852，零失败／跳过 | pr12-debug.log／trx |
| Release完整 | 852/852，零失败／跳过 | pr12-release.log／trx |
| x64／ARM64 MSIX | 签名及内容核验通过 | pr12-release.log、pr12-arm64-package.log |
| ARM64测试 | 仅交叉编译通过，无实机运行 | pr12-arm64-tests-build.log |

回归使用本地协议夹具与隔离NTFS／CFAPI根，不访问已取消NAS、不创建真实远端测试目录。未执行GUI／Explorer目视、真实服务兼容性、安装升级和ARM64实机，保留待验收；历史Shell和其他审查待办保持。

## 产物

| 架构 | 路径 | SHA256 |
| --- | --- | --- |
| x64 | artifacts/packages/x64/HybridMount_0.0.9.17_x64_Test/HybridMount_0.0.9.17_x64.msix | 5EE8D324126FB2898A5B8F1B8E289C40B5A4261D1B986DF2CA4C375A9E64E184 |
| ARM64 | artifacts/packages/ARM64/HybridMount_0.0.9.17_arm64_Test/HybridMount_0.0.9.17_arm64.msix | DE8D64E4D2A260976F175CF4BDE119005399F88B4DB5E09F314C2F4E098192B5 |

汇总见 Validation-PR-Merges.md；机器可读证据为artifacts/validation/pr12-evidence.json。
