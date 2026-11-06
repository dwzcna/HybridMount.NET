# HybridMount 0.0.9.20 验收记录

2026-11-06，Windows x64。[PR #15](https://github.com/dwzcna/HybridMount.NET/pull/15)：S3大对象分片复制与元数据保留。本次为逐个合并的第15/15项，独立递增第四段版本；不推送。

## 合并内容

- 来源：codex/pr-15-s3-multipart-copy，提交d859743cd6f28d77f68cb0be71c1710fb2f164ce。合并前master为7b88a57548b3bd61d2686fe282f7cc08bb44d24d；三方结果先经受检补丁应用，再统一程序集、清单和README版本。
- 超过5 GiB使用服务端分片复制，不先下载整份对象；源版本与每个范围均条件核对，保留Content-Type及用户元数据，完成后继续核对目标回执并条件删源。集成PR6语义后显式要求MultipartComplete为Supported才初始化用户目标，新增缓存不支持结果下连续两次移动均零用户初始化／分片的回归。未知初始化回执及初始化前中断继续沿用PR7保留无UploadId意图的安全边界，调整原时间窗猜测清理测试；已知精确UploadId的失败清理、恢复及独立取消保持。多个请求仍不构成原子移动。
最终x64非打包Release独立构建通过，未启动或安装；EXE及程序集版本均为0.0.9.20。MSBuild实际编译项主工程182、测试240，本批相关文件分别覆盖28／52项，零重复、零遗漏。
- 保留三个工程、正式配置及同步根；本批未切换、重启或强杀既有0.0.9.5程序，不安装本轮中间构建。

## 实际自动验证

| 项目 | 结果 | 证据（artifacts/validation） |
| --- | --- | --- |
| x64最小WinUI／原生 | 通过 | pr15-minimal.log |
| 针对性 | 89/89，零失败／跳过 | pr15-targeted.log／trx |
| Debug完整 | 940/940，零失败／跳过 | pr15-debug.log／trx |
| Release完整 | 940/940，零失败／跳过 | pr15-release.log／trx |
| x64／ARM64 MSIX | 签名及内容核验通过 | pr15-release.log、pr15-arm64-package.log |
| ARM64测试 | 仅交叉编译通过，无实机运行 | pr15-arm64-tests-build.log |
| x64非打包Release | 构建及EXE／程序集版本0.0.9.20核对通过，未启动 | pr15-unpackaged-release.log、pr15-final-build-audit.json |
| 编译项审计 | 主工程182／测试240，无重复；本批28／52文件无遗漏 | pr15-main-compile.json、pr15-tests-compile.json、pr15-compile-audit.json |

回归使用本地协议夹具与隔离NTFS／CFAPI根，不访问已取消NAS、不创建真实远端测试目录。未执行GUI／Explorer目视、真实服务兼容性、安装升级和ARM64实机，保留待验收；历史Shell和其他审查待办保持。

## 产物

| 架构 | 路径 | SHA256 |
| --- | --- | --- |
| x64 | artifacts/packages/x64/HybridMount_0.0.9.20_x64_Test/HybridMount_0.0.9.20_x64.msix | 02561D3801BA8F01824E4046C729FF14C7517CE698FAA0C74C05D5226090D291 |
| ARM64 | artifacts/packages/ARM64/HybridMount_0.0.9.20_arm64_Test/HybridMount_0.0.9.20_arm64.msix | D27465A6A9BF1F952A6116C9B0A274A5D359B562283A9BD5EBD2FB2502D280C8 |

汇总见 Validation-PR-Merges.md；机器可读证据为artifacts/validation/pr15-evidence.json。

## 保留边界

- PR #7缺少UploadId的初始化意图仍保留待核对，未实现安全自动精确回收；不以键名或时间猜测所有权。已知精确ID的恢复继续可用。
- 分片复制的超5 GiB范围与协议头以本地服务模拟元数据验证，未执行真实5 GiB对象移动或服务兼容性认证。S3多请求移动仍存在目标核对后的并发窗口。
