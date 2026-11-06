# HybridMount 0.0.9.14 验收记录

2026-11-06，Windows x64。[PR #9](https://github.com/dwzcna/HybridMount.NET/pull/9)：WebDAV加锁更新减少重复传输。本次为逐个合并的第9/15项，独立递增第四段版本；不推送。

## 合并内容

- 来源：codex/pr-9-webdav-save-transfers，提交e708fd35247c198439249e53e9b3f624a05cc15d。合并前master为1142bb9add6481873649db337124ca75c42e2c5b；三方结果先经受检补丁应用，再统一程序集、清单和README版本。
- 保留已验证排他锁及上传后可靠强版本下省去一次读回的安全快路径。集成时删除仅凭弱标签、大小、修改时间跳过原始摘要的分支，锁建立前的同元数据外部改写仍先核对基线；上传后弱版本或仅时间变化仍读回摘要。新增同弱版本同长度同时间的外部编辑，以及同长度写坏且时间变化的保护回归；锁失效、条件更新和未知结果分类保持。
- 保留三个工程、正式配置及同步根；本批未切换、重启或强杀既有0.0.9.5程序，不安装本轮中间构建。

## 实际自动验证

| 项目 | 结果 | 证据（artifacts/validation） |
| --- | --- | --- |
| x64最小WinUI／原生 | 通过 | pr09-minimal.log |
| 针对性 | 31/31，零失败／跳过 | pr09-targeted.log／trx |
| Debug完整 | 783/783，零失败／跳过 | pr09-debug.log／trx |
| Release完整 | 783/783，零失败／跳过 | pr09-release.log／trx |
| x64／ARM64 MSIX | 签名及内容核验通过 | pr09-release.log、pr09-arm64-package.log |
| ARM64测试 | 仅交叉编译通过，无实机运行 | pr09-arm64-tests-build.log |

回归使用本地协议夹具与隔离NTFS／CFAPI根，不访问已取消NAS、不创建真实远端测试目录。未执行GUI／Explorer目视、真实服务兼容性、安装升级和ARM64实机，保留待验收；历史Shell和其他审查待办保持。

## 产物

| 架构 | 路径 | SHA256 |
| --- | --- | --- |
| x64 | artifacts/packages/x64/HybridMount_0.0.9.14_x64_Test/HybridMount_0.0.9.14_x64.msix | 8A0A1098B4AF1854CCED7044D19C41B9C2A00812479BE343D50B57C19C94184F |
| ARM64 | artifacts/packages/ARM64/HybridMount_0.0.9.14_arm64_Test/HybridMount_0.0.9.14_arm64.msix | C742E49A9591D68B40E9938BC8747024FB78A675EAE03F8ACEE4879B9601EAD3 |

汇总见 Validation-PR-Merges.md；机器可读证据为artifacts/validation/pr09-evidence.json。
