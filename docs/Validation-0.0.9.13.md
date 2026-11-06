# HybridMount 0.0.9.13 验收记录

2026-11-06，Windows x64。[PR #8](https://github.com/dwzcna/HybridMount.NET/pull/8)：WebDAV临时资源元数据清理。本次为逐个合并的第8/15项，独立递增第四段版本；不推送。

## 合并内容

- 来源：codex/pr-8-webdav-temp-cleanup，提交b78e24b8834beb68bdab3d17b28dcdd3dc350ea6。合并前master为c92654995ee9ed806fd5980b96e0063dca47d47a；三方结果先经受检补丁应用，再统一程序集、清单和README版本。
- 保留可靠写回强ETag的免GET条件清理；没有写回强ETag时不把之后读取的元数据升级为所有权，必须在当前强版本下核对日志摘要、复核版本，再If-Match删除。弱ETag、长度和修改时间不足以删除，持续无强版本保持CleanupPending。摘要核对移到可取消的后台队列，持久退避、五次上限及释放排空保持。新增无回执后同长度替换和弱标签同大小同时间外部内容保护。首轮针对性、Debug及Release的旧同步清理时序失败证据保留，已改为显式等待后台完成，不降低原有内容和零残留断言；最后仅测试等待调整，生产代码与通过的Debug全量相同，相关Debug用例及Release完整复测。
- 保留三个工程、正式配置及同步根；本批未切换、重启或强杀既有0.0.9.5程序，不安装本轮中间构建。

## 实际自动验证

| 项目 | 结果 | 证据（artifacts/validation） |
| --- | --- | --- |
| x64最小WinUI／原生 | 通过 | pr08-minimal.log |
| 针对性 | 35/35，零失败／跳过 | pr08-targeted.log／trx |
| Debug完整 | 764/764，零失败／跳过 | pr08-debug.log／trx |
| Release完整 | 764/764，零失败／跳过 | pr08-release.log／trx |
| x64／ARM64 MSIX | 签名及内容核验通过 | pr08-release.log、pr08-arm64-package.log |
| ARM64测试 | 仅交叉编译通过，无实机运行 | pr08-arm64-tests-build.log |

回归使用本地协议夹具与隔离NTFS／CFAPI根，不访问已取消NAS、不创建真实远端测试目录。未执行GUI／Explorer目视、真实服务兼容性、安装升级和ARM64实机，保留待验收；历史Shell和其他审查待办保持。

## 产物

| 架构 | 路径 | SHA256 |
| --- | --- | --- |
| x64 | artifacts/packages/x64/HybridMount_0.0.9.13_x64_Test/HybridMount_0.0.9.13_x64.msix | 2832E95FCC44711667F16C32B95F4985398121D29B0359FE70F3476D247E8E57 |
| ARM64 | artifacts/packages/ARM64/HybridMount_0.0.9.13_arm64_Test/HybridMount_0.0.9.13_arm64.msix | AF7018ED7F64EA04585AB53B7F726EF8D625AC9FFB548CDE73F19B650AB5E484 |

汇总见 Validation-PR-Merges.md；机器可读证据为artifacts/validation/pr08-evidence.json。
