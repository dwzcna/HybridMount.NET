# HybridMount 0.0.9.19 验收记录

2026-11-06，Windows x64。[PR #14](https://github.com/dwzcna/HybridMount.NET/pull/14)：S3目录幂等移动与部分清理结果。本次为逐个合并的第14/15项，独立递增第四段版本；不推送。

## 合并内容

- 来源：codex/pr-14-s3-directory-move，提交58b22f784b4eaf6d444682969f35ed21f387659e。合并前master为d9a911c1a9d225080a6aa11dd78967f580f0e86b；三方结果先经受检补丁应用，再统一程序集、清单和README版本。
- 在对象树中保留PR5复制前源版本重查及PR4流式回退，同时接入目录移动专用目标根边界。已有目标集合可复用且保留无关内容，子文件仍条件新增。移动收集阶段逐目录核实真实标记，兼容PR5无逐项HEAD列表；新增嵌套显式空目录保留及虚拟父目录不造标记回归。仅源目录收尾失败独立告警并保留源；文件复制／删源失败仍报告部分结果，观察者异常不改变移动结果。
- 保留三个工程、正式配置及同步根；本批未切换、重启或强杀既有0.0.9.5程序，不安装本轮中间构建。

## 实际自动验证

| 项目 | 结果 | 证据（artifacts/validation） |
| --- | --- | --- |
| x64最小WinUI／原生 | 通过 | pr14-minimal.log |
| 针对性 | 93/93，零失败／跳过 | pr14-targeted.log／trx |
| Debug完整 | 915/915，零失败／跳过 | pr14-debug.log／trx |
| Release完整 | 915/915，零失败／跳过 | pr14-release.log／trx |
| x64／ARM64 MSIX | 签名及内容核验通过 | pr14-release.log、pr14-arm64-package.log |
| ARM64测试 | 仅交叉编译通过，无实机运行 | pr14-arm64-tests-build.log |

回归使用本地协议夹具与隔离NTFS／CFAPI根，不访问已取消NAS、不创建真实远端测试目录。未执行GUI／Explorer目视、真实服务兼容性、安装升级和ARM64实机，保留待验收；历史Shell和其他审查待办保持。

## 产物

| 架构 | 路径 | SHA256 |
| --- | --- | --- |
| x64 | artifacts/packages/x64/HybridMount_0.0.9.19_x64_Test/HybridMount_0.0.9.19_x64.msix | 478907E0C4C53F6F99685A597FCC7EC2B8D8EDC1EFCC75A829717828F911AD69 |
| ARM64 | artifacts/packages/ARM64/HybridMount_0.0.9.19_arm64_Test/HybridMount_0.0.9.19_arm64.msix | C75C83EB98E0175484C16DEB6FFE0961A9A649D74FA23F4869C4AAA42966DFB9 |

汇总见 Validation-PR-Merges.md；机器可读证据为artifacts/validation/pr14-evidence.json。
