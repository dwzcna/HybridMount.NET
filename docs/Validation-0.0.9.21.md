# HybridMount 0.0.9.21 验收记录

2026-11-06，Windows x64。[PR #16](https://github.com/dwzcna/HybridMount.NET/pull/16)：协议探测作用域与权限回退。本次为本轮继续逐个合并的第1/3项（累计PR #16），独立递增第四段版本；不推送master。

## 合并内容

- 来源：codex/pr-16-s3-probe-scope，提交78b99e591768ffb2d9a06f657d0db9221d441df8。合并前master为807d5540ff3cf2cb9533554a32f241f4fa8ba180；三方结果先经受检补丁应用，再统一程序集、清单和README版本。
- S3／DAV能力探测默认在配置挂载根进行，根明确拒绝写入时一次回退到本操作目标父目录并按连接复用结果；其他故障不换位置。写入前登记精确探测路径，列表、完整快照、SQS提示和RFC6578增量排除本会话内部项，保留未登记同名近似项及Foreign可见性。对象树冲突同时保留PR5坏条目隔离、无逐项HEAD枚举、语义版本核对及此前分片回退。补齐初始DAV基线404先校验再过滤，适配PR8后台摘要清理等待，不降低摘要、强ETag和外部内容保护。旧15个本地及GitHub来源分支已按祖先关系／SHA租约逐个清理，主分支未推送；证据分别保存。
- 保留三个工程、正式配置及同步根；本批未切换、重启或强杀既有0.0.9.5程序，不安装本轮中间构建。

## 实际自动验证

| 项目 | 结果 | 证据（artifacts/validation） |
| --- | --- | --- |
| x64最小WinUI／原生 | 通过 | pr16-minimal.log |
| 针对性 | 150/150，零失败／跳过 | pr16-targeted.log／trx |
| Debug完整 | 991/991，零失败／跳过 | pr16-debug.log／trx |
| Release完整 | 991/991，零失败／跳过 | pr16-release.log／trx |
| x64／ARM64 MSIX | 签名及内容核验通过 | pr16-release.log、pr16-arm64-package.log |
| ARM64测试 | 仅交叉编译通过，无实机运行 | pr16-arm64-tests-build.log |

回归使用本地协议夹具与隔离NTFS／CFAPI根，不访问已取消NAS、不创建真实远端测试目录。未执行GUI／Explorer目视、真实服务兼容性、安装升级和ARM64实机，保留待验收；历史Shell和其他审查待办保持。

## 产物

| 架构 | 路径 | SHA256 |
| --- | --- | --- |
| x64 | artifacts/packages/x64/HybridMount_0.0.9.21_x64_Test/HybridMount_0.0.9.21_x64.msix | B6593FEAB74E59F8E837AEC80DE31F024F4F7BC00DAF7F7335AF5FB378938C5C |
| ARM64 | artifacts/packages/ARM64/HybridMount_0.0.9.21_arm64_Test/HybridMount_0.0.9.21_arm64.msix | 0D9693D006A31955B651B656A9BA0FBDB4B49AB85724664A9D28D48EC4EF6E81 |

汇总见 Validation-PR-Merges.md；机器可读证据为artifacts/validation/pr16-evidence.json。
