# HybridMount 0.0.9.15 验收记录

2026-11-06，Windows x64。[PR #10](https://github.com/dwzcna/HybridMount.NET/pull/10)：上传准备与父目录操作移出根锁。本次为逐个合并的第10/15项，独立递增第四段版本；不推送。

## 合并内容

- 来源：codex/pr-10-upload-gate-scope，提交c19ae6ccdbdb6921fa99f69b3d494ce9ff9d2b24。合并前master为a9f90b83d643bde6b004f4242ee46bcd26c409c4；三方结果先经受检补丁应用，再统一程序集、清单和README版本。
- 在对象树内解决模型、快照和工作器冲突，同时保留自动重试／退避字段、复用快照的计数和PR4流式核对。捕获与条件发布短暂持根锁，复制／哈希、水合和远端父目录操作移到锁外；尝试ID和原子失败更新防止迟到结果覆盖较新代次或取消。补充协议包装内永久证书／权限诊断的递归分类：发送前失败，不无限按瞬时故障重排；已发送仍保留OutcomeUnknown，新增分类及持久阶段回归。F01提交戳、类型冲突、删除屏障与同名替换保护保持。
首轮两项旧测试在BeforePrepare屏障内另行改写状态，已被新增迟到结果保护拒绝覆盖；改为先保存目标阶段再抛错，继续断言发送前Waiting和发送后OutcomeUnknown，新代次／状态替换保护另有独立覆盖。失败证据保留于pr10-targeted-first-failed。
- 保留三个工程、正式配置及同步根；本批未切换、重启或强杀既有0.0.9.5程序，不安装本轮中间构建。

## 实际自动验证

| 项目 | 结果 | 证据（artifacts/validation） |
| --- | --- | --- |
| x64最小WinUI／原生 | 通过 | pr10-minimal.log |
| 针对性 | 125/125，零失败／跳过 | pr10-targeted.log／trx |
| Debug完整 | 812/812，零失败／跳过 | pr10-debug.log／trx |
| Release完整 | 812/812，零失败／跳过 | pr10-release.log／trx |
| x64／ARM64 MSIX | 签名及内容核验通过 | pr10-release.log、pr10-arm64-package.log |
| ARM64测试 | 仅交叉编译通过，无实机运行 | pr10-arm64-tests-build.log |

回归使用本地协议夹具与隔离NTFS／CFAPI根，不访问已取消NAS、不创建真实远端测试目录。未执行GUI／Explorer目视、真实服务兼容性、安装升级和ARM64实机，保留待验收；历史Shell和其他审查待办保持。

## 产物

| 架构 | 路径 | SHA256 |
| --- | --- | --- |
| x64 | artifacts/packages/x64/HybridMount_0.0.9.15_x64_Test/HybridMount_0.0.9.15_x64.msix | DB2FC318F72487CC60796A1F79F4A5FAFB78A02CC833692B42093076AD1691AD |
| ARM64 | artifacts/packages/ARM64/HybridMount_0.0.9.15_arm64_Test/HybridMount_0.0.9.15_arm64.msix | 5BCAEC1EEFF754F4616B588E1B870A15658506A76F91D5E082B37E666306A913 |

汇总见 Validation-PR-Merges.md；机器可读证据为artifacts/validation/pr10-evidence.json。
