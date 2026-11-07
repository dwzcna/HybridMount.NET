# 0.0.9.27：PR #20合并验收

日期：2026-11-07。按用户要求逐个拉取、逐个合并到master，每次递增第四段版本。本版来源为[本地监控按一次性文件戳过滤水合与占位自事件](https://github.com/dwzcna/HybridMount.NET/pull/20)，精确提交90e01cabbeba69ecffaa7079a4aad915ac4eaac1，合并前master为0abab2830b059ad0856610d48c08e20b860e70f0。

## 代码完成

将水合、占位创建、元数据及固定内容更新后的可靠文件戳记入会话内有界缓存；两秒内完整戳一致才一次性过滤Changed／Created文件范围，保留父目录范围。外部编辑、删除、重命名、过期、属性读取失败或USN不可用仍正常扫描；六十秒全量兜底、溢出及失败补扫保持。属性句柄不读正文或触发水合，断开释放时解除通知订阅，并保留PR #19的提交代次保护。

来源先经Git对象树三方合并预演，差异生成版本补丁，经git apply --check后应用；提交保留来源第二父节点。版本、程序集和MSIX统一0.0.9.27。必要中文职责说明随本版补齐，不增加参考仓库说明；保留三个工程、现有SDK、IPC v2、SQLite及配置格式。

## 实际验证

| 项目 | 结果 | artifacts/validation证据 |
| --- | --- | --- |
| x64 Debug最小编译 | 通过 | pr20-minimal.log |
| 针对性回归 | 130/130，失败0、跳过0 | pr20-targeted.log／trx |
| x64 Debug全量 | 1084/1084，失败0、跳过0 | pr20-debug.log／trx |
| x64 Release全量 | 1084/1084，失败0、跳过0 | pr20-release.log／trx |
| x64／ARM64签名MSIX | 签名及内容核验通过 | pr20-release.log、pr20-arm64-package.log |
| ARM64测试 | 仅交叉编译通过，未实机运行 | pr20-arm64-tests-build.log |
| x64非打包Release | 构建通过，未启动／安装 | pr20-unpackaged.log |
| 实际Compile登记 | 主188／测试252，相关文件无重复遗漏 | pr20-compile-main.json、pr20-compile-tests.json、pr20-evidence.json |

针对性及全量结果均为本版实际执行，未将来源PR描述或历史结果记为测试通过。

## 产物

| 架构 | 签名MSIX | SHA256 |
| --- | --- | --- |
| x64 | artifacts/packages/x64/HybridMount_0.0.9.27_x64_Test/HybridMount_0.0.9.27_x64.msix | 6A24EBF862A18E3603D2B24AF94FE5608C67A7D5784A6CB83EB0DB061307E996 |
| ARM64 | artifacts/packages/ARM64/HybridMount_0.0.9.27_arm64_Test/HybridMount_0.0.9.27_arm64.msix | 7C2B3018D626C642809D8A8F6FAFE331E5D5CA971176D5F6123A143066234E20 |

## 限制与发布

自动测试仅使用本地服务及隔离NTFS／CFAPI根，未访问已取消NAS、未连接真实WebDAV或S3、未加载或修改正式配置及同步根。GUI／Explorer、安装升级和ARM64实机未执行；历史Shell和审查待办继续保留。

本版独立双父合并提交的父节点、来源祖先及版本由本批审计核对；全部PR完成后master普通推送GitHub和Azure，再按精确SHA逐个删除已经发布合并的远程来源。发生来源更新时不删除旧清单对应的新提交。发布结果保存在artifacts/validation/pr-round3-publication.json。
